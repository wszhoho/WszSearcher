using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WszSearcher.Core.Native;

/// <summary>USN Journal 相关 Win32 API P/Invoke 封装</summary>
internal static class UsnApi
{
    // ─── FSCTL 控制码 ───
    internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    internal const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
    internal const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    // ─── 访问权限 ───
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    // ─── USN 原因掩码（过滤感兴趣的变更） ───
    internal const uint USN_REASON_FILE_CREATE = 0x00000100;
    internal const uint USN_REASON_FILE_DELETE = 0x00000200;
    internal const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    internal const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    internal const uint USN_REASON_CLOSE = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>打开卷设备（如 \\.\C:）</summary>
    internal static SafeFileHandle OpenVolume(string volume)
    {
        return CreateFile(
            volume,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,  // 去掉 FILE_FLAG_NO_BUFFERING，避免扇区对齐问题
            IntPtr.Zero);
    }

    /// <summary>查询 USN Journal 信息</summary>
    internal static bool QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData data)
    {
        data = default;
        var outBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UsnJournalData>());
        try
        {
            if (!DeviceIoControl(
                    volumeHandle,
                    FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0,
                    outBuffer, (uint)Marshal.SizeOf<UsnJournalData>(),
                    out _,
                    IntPtr.Zero))
                return false;

            data = Marshal.PtrToStructure<UsnJournalData>(outBuffer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    /// <summary>枚举 MFT 记录（全量扫描用）</summary>
    internal static IEnumerable<UsnRecord> EnumerateUsnRecords(SafeFileHandle volumeHandle, ulong journalId)
    {
        var enumData = new MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };

        var inBufSize = Marshal.SizeOf<MftEnumDataV0>();
        var inBuffer = Marshal.AllocHGlobal(inBufSize);
        var outBufferSize = 65536; // 64KB 缓存
        var outBuffer = Marshal.AllocHGlobal(outBufferSize);

        // USN_RECORD_V2 最小头部大小（不含文件名）
        const int minUsnHeaderSize = 60;

        try
        {
            Marshal.StructureToPtr(enumData, inBuffer, false);

            while (true)
            {
                if (!DeviceIoControl(
                        volumeHandle,
                        FSCTL_ENUM_USN_DATA,
                        inBuffer, (uint)inBufSize,
                        outBuffer, (uint)outBufferSize,
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != 38) // ERROR_HANDLE_EOF 是正常结束，不警告
                        AppLog.Warn("fname", $"[USN] Enumerate 失败, error={err}");
                    break;
                }

                // 至少需要一个完整记录头
                if (bytesReturned < minUsnHeaderSize)
                {
                    AppLog.Warn("fname", $"[USN] bytesReturned({bytesReturned}) < min({minUsnHeaderSize})");
                    break;
                }

                // 输出缓冲前 8 字节是下次起始 FRN，记录从偏移 8 开始
                if (bytesReturned < 8 + minUsnHeaderSize)
                {
                    AppLog.Warn("fname", $"[USN] bytesReturned({bytesReturned}) < 8+min({minUsnHeaderSize})");
                    break;
                }

                var offset = 8;
                UsnRecord? lastValidRecord = null;

                while (offset + minUsnHeaderSize <= bytesReturned)
                {
                    var record = MarshalUsnRecord(outBuffer + offset);
                    if (record.RecordLength == 0 || (int)record.RecordLength <= 0)
                        break;

                    // 防御：RecordLength 必须 >= 最小头部大小
                    if (record.RecordLength < minUsnHeaderSize)
                    {
                        offset += minUsnHeaderSize; // 跳过损坏的记录
                        continue;
                    }

                    // 防止损坏记录导致 offset 溢出缓冲区
                    if (offset + (int)record.RecordLength > bytesReturned)
                        break;

                    yield return record;
                    lastValidRecord = record;
                    offset += (int)record.RecordLength;
                }

                // 继续枚举直到 DeviceIoControl 失败

                // 用输出缓冲前 8 字节更新下一次的起始 FRN
                enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(outBuffer, 0);
                Marshal.StructureToPtr(enumData, inBuffer, false);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    /// <summary>读取 USN Journal 变更（增量用）</summary>
    internal static IEnumerable<UsnRecord> ReadUsnJournal(
        SafeFileHandle volumeHandle, ulong journalId, long startUsn)
    {
        var readData = new ReadUsnJournalDataV0
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF, // 所有变更
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId,
            MinMajorVersion = 2,
            MaxMajorVersion = 2  // 只请求 V2 记录（兼容当前解析代码）
        };

        var inBufSize = Marshal.SizeOf<ReadUsnJournalDataV0>();
        var inBuffer = Marshal.AllocHGlobal(inBufSize);
        var outBufferSize = 65536;
        var outBuffer = Marshal.AllocHGlobal(outBufferSize);

        try
        {
            Marshal.StructureToPtr(readData, inBuffer, false);
            const int minUsnHeaderSize = 60;

            while (true)
            {
                if (!DeviceIoControl(
                        volumeHandle,
                        FSCTL_READ_USN_JOURNAL,
                        inBuffer, (uint)inBufSize,
                        outBuffer, (uint)outBufferSize,
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    AppLog.Warn("fname", $"[USN] ReadUsnJournal DeviceIoControl 失败, error={err}");
                    break;
                }

                AppLog.Info("fname", $"[USN] ReadUsnJournal 返回 {bytesReturned} 字节");
                // 前 8 字节是 NextUsn，记录数据从偏移 8 开始
                if (bytesReturned < 8 + minUsnHeaderSize) break;

                var offset = 8;
                UsnRecord? lastValidRecord = null;

                while (offset + minUsnHeaderSize <= bytesReturned)
                {
                    var record = MarshalUsnRecord(outBuffer + offset);
                    if (record.RecordLength == 0 || (int)record.RecordLength <= 0) break;
                    if (record.RecordLength < minUsnHeaderSize)
                    {
                        offset += minUsnHeaderSize;
                        continue;
                    }

                    // 防止损坏记录导致 offset 溢出缓冲区
                    if (offset + (int)record.RecordLength > bytesReturned)
                        break;

                    yield return record;
                    lastValidRecord = record;
                    offset += (int)record.RecordLength;
                }

                if (bytesReturned < outBufferSize)
                    break;

                // 用输出缓冲前 8 字节（NextUsn）更新起始 USN
                readData.StartUsn = Marshal.ReadInt64(outBuffer, 0);
                Marshal.StructureToPtr(readData, inBuffer, false);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
            Marshal.FreeHGlobal(outBuffer);
        }
    }

    private static UsnRecord MarshalUsnRecord(IntPtr ptr)
    {
        // 读取固定部分（USN_RECORD_V2 头部 60 字节）
        var recordLength = (uint)Marshal.ReadInt32(ptr, 0);
        var majorVersion = (ushort)Marshal.ReadInt16(ptr, 4);
        var minorVersion = (ushort)Marshal.ReadInt16(ptr, 6);

        var isV3 = majorVersion >= 3;
        var usnOff = isV3 ? 40 : 24;
        var timeOff = isV3 ? 48 : 32;
        var reasonOff = isV3 ? 56 : 40;
        var fnLenOff = isV3 ? 72 : 56;
        var fnOffOff = isV3 ? 74 : 58;
        var pfRefOff = isV3 ? 24 : 16;

        var fileRefNumber = (ulong)Marshal.ReadInt64(ptr, 8);
        var parentFileRefNumber = (ulong)Marshal.ReadInt64(ptr, pfRefOff);
        var usn = Marshal.ReadInt64(ptr, usnOff);
        var timeStamp = Marshal.ReadInt64(ptr, timeOff);
        DateTime ft;
        try { ft = DateTime.FromFileTimeUtc(timeStamp); }
        catch { ft = DateTime.MinValue; }
        var reason = (uint)Marshal.ReadInt32(ptr, reasonOff);
        var sourceInfo = (uint)Marshal.ReadInt32(ptr, reasonOff + 4);
        var securityId = (uint)Marshal.ReadInt32(ptr, reasonOff + 8);
        var fileAttributes = (uint)Marshal.ReadInt32(ptr, reasonOff + 12);
        var fileNameLength = (ushort)Marshal.ReadInt16(ptr, fnLenOff);
        var fileNameOffset = (ushort)Marshal.ReadInt16(ptr, fnOffOff);

        // 防止损坏记录导致越界读取 AccessViolationException
        const int minUsnHeaderSize = 60;
        string fileName;
        if (fileNameOffset >= minUsnHeaderSize &&
            fileNameLength > 0 &&
            fileNameOffset + fileNameLength <= recordLength)
        {
            fileName = Marshal.PtrToStringUni(ptr + fileNameOffset, fileNameLength / 2) ?? "";
        }
        else
        {
            fileName = ""; // 损坏记录，跳过文件名
        }

        return new UsnRecord
        {
            RecordLength = recordLength,
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            FileReferenceNumber = fileRefNumber,
            ParentFileReferenceNumber = parentFileRefNumber,
            Usn = usn,
            TimeStamp = ft,
            Reason = reason,
            SourceInfo = sourceInfo,
            SecurityId = securityId,
            FileAttributes = fileAttributes,
            FileName = fileName,
            FileNameLength = fileNameLength
        };
    }
}

// ─── Win32 数据结构 ───

[StructLayout(LayoutKind.Sequential)]
internal struct UsnJournalData
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MftEnumDataV0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ReadUsnJournalDataV0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
    public ushort MinMajorVersion;
    public ushort MaxMajorVersion;
}

/// <summary>USN 记录托管版本</summary>
internal class UsnRecord
{
    public uint RecordLength { get; init; }
    public ushort MajorVersion { get; init; }
    public ushort MinorVersion { get; init; }
    public ulong FileReferenceNumber { get; init; }
    public ulong ParentFileReferenceNumber { get; init; }
    public long Usn { get; init; }
    public DateTime TimeStamp { get; init; }
    public uint Reason { get; init; }
    public uint SourceInfo { get; init; }
    public uint SecurityId { get; init; }
    public uint FileAttributes { get; init; }
    public string FileName { get; init; } = "";
    public ushort FileNameLength { get; init; }

    public bool IsDirectory => (FileAttributes & 0x10) != 0;
    public bool IsDeleted => (Reason & 0x200) != 0;

    public override string ToString() => FileName;
}
