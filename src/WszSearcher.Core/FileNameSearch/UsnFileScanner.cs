using System.Diagnostics;
using WszSearcher.Core.Native;

namespace WszSearcher.Core.FileNameSearch;

/// <summary>
/// NTFS USN Journal 文件扫描器——读取卷的 MFT 记录来构建全量文件索引
/// 这是实现 Everything 级速度的核心
/// </summary>
public class UsnFileScanner
{
    private readonly string _volume;
    private readonly string _volumeRoot;

    /// <summary>扫描进度报告</summary>
    public event Action<int>? ProgressChanged;
    /// <summary>扫描状态报告</summary>
    public event Action<string>? StatusChanged;

    /// <summary>是否已取消</summary>
    public CancellationToken CancellationToken { get; set; }

    public UsnFileScanner(char driveLetter)
    {
        _volume = $@"\\.\{driveLetter}:";
        _volumeRoot = $@"{driveLetter}:\";
    }

    /// <summary>执行全量扫描，返回文件记录列表</summary>
    public async Task<List<FileRecord>> ScanAllAsync()
    {
        return await Task.Run(() => ScanAll());
    }

    private List<FileRecord> ScanAll()
    {
        var result = new List<FileRecord>();
        var sw = Stopwatch.StartNew();

        StatusChanged?.Invoke("打开卷设备...");
        using var volumeHandle = UsnApi.OpenVolume(_volume);
        if (volumeHandle.IsInvalid)
        {
            StatusChanged?.Invoke($"无法访问卷 {_volume}，将使用目录遍历方式（需要管理员权限运行以获得最大速度）");
            return FallbackScan();
        }

        StatusChanged?.Invoke("正在读取 USN Journal...");
        if (!UsnApi.QueryUsnJournal(volumeHandle, out var journalData))
        {
            StatusChanged?.Invoke("USN Journal 不可用，使用目录遍历方式");
            return FallbackScan();
        }

        StatusChanged?.Invoke($"正在枚举 MFT 记录（USN范围: {journalData.FirstUsn} ~ {journalData.NextUsn}）...");
        
        // Phase 1: 扫描所有 USN 记录
        var records = new Dictionary<ulong, UsnRecord>();
        var count = 0;
        const int batchSize = 10000;
        var lastProgressReport = DateTime.MinValue;

        foreach (var record in UsnApi.EnumerateUsnRecords(volumeHandle, journalData.UsnJournalID))
        {
            CancellationToken.ThrowIfCancellationRequested();

            // 只处理文件，跳过目录（目录作为路径的一部分处理）
            if (!record.IsDeleted && !string.IsNullOrEmpty(record.FileName))
            {
                records[record.FileReferenceNumber] = record;
                count++;
            }

            // 每 10000 条或每秒报告进度
            if (count % batchSize == 0 || (DateTime.UtcNow - lastProgressReport).TotalSeconds >= 1)
            {
                lastProgressReport = DateTime.UtcNow;
                ProgressChanged?.Invoke(count);
                StatusChanged?.Invoke($"已扫描 {count} 条记录...");
            }
        }

        StatusChanged?.Invoke($"MFT 扫描完成，共 {count} 条记录，正在解析路径...");

        // Phase 2: 解析完整路径
        var resolved = 0;
        foreach (var (frn, record) in records)
        {
            CancellationToken.ThrowIfCancellationRequested();

            var fullPath = ResolvePath(record.ParentFileReferenceNumber, record.FileName, records);
            if (fullPath != null)
            {
                // 拼接盘符前缀，确保与 FileSystemWatcher 事件的绝对路径一致
                var absolutePath = _volumeRoot + fullPath;
                result.Add(new FileRecord
                {
                    FileName = record.FileName,
                    FullPath = absolutePath,
                    Directory = Path.GetDirectoryName(absolutePath) ?? "",
                    LastModified = record.TimeStamp.ToLocalTime(),
                    FileReferenceNumber = frn,
                    ParentFileReferenceNumber = record.ParentFileReferenceNumber,
                    IsDirectory = record.IsDirectory
                });
            }

            resolved++;
            if (resolved % batchSize == 0)
            {
                StatusChanged?.Invoke($"正在解析路径... {resolved}/{count}");
            }
        }

        sw.Stop();
        StatusChanged?.Invoke($"扫描完成！共 {result.Count} 个文件，耗时 {sw.Elapsed.TotalSeconds:F1} 秒");

        return result;
    }

    /// <summary>通过父引用递归解析完整路径</summary>
    private static string? ResolvePath(ulong parentFrn, string fileName, Dictionary<ulong, UsnRecord> records)
    {
        // 文件名跳过特殊条目
        if (fileName == "." || fileName == "..") return null;

        var pathSegments = new List<string> { fileName };
        var currentParent = parentFrn;
        var maxDepth = 100; // 防止循环引用

        while (currentParent != 0 && maxDepth-- > 0)
        {
            if (!records.TryGetValue(currentParent, out var parentRecord))
                break;

            if (parentRecord.FileName == "." || parentRecord.FileName == ".." || parentRecord.FileName == "")
                break;

            pathSegments.Add(parentRecord.FileName);

            if (parentRecord.ParentFileReferenceNumber == currentParent)
                break; // 自引用循环保护

            currentParent = parentRecord.ParentFileReferenceNumber;
        }

        // 深度耗尽（可能循环引用或极深目录树），返回 null 避免不完整路径
        if (maxDepth <= 0 && currentParent != 0)
            return null;

        pathSegments.Reverse();
        return string.Join("\\", pathSegments);
    }

    /// <summary>备用方案：使用目录遍历（非管理员模式）</summary>
    private List<FileRecord> FallbackScan()
    {
        var result = new List<FileRecord>();
        var sw = Stopwatch.StartNew();

        var dirs = new Queue<string>();
        dirs.Enqueue(_volumeRoot);

        while (dirs.Count > 0)
        {
            CancellationToken.ThrowIfCancellationRequested();

            var dir = dirs.Dequeue();
            try
            {
                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };

                foreach (var subDir in Directory.EnumerateDirectories(dir, "*", enumOptions))
                {
                    // 额外检查：跳过以 . 开头的目录和常见的依赖目录
                    var dirName = Path.GetFileName(subDir);
                    if (dirName.StartsWith('.') || dirName == "node_modules" || dirName == ".git")
                        continue;
                    dirs.Enqueue(subDir);
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        result.Add(new FileRecord
                        {
                            FileName = fi.Name,
                            FullPath = fi.FullName,
                            Directory = fi.DirectoryName ?? "",
                            FileSize = fi.Length,
                            LastModified = fi.LastWriteTime,
                            IsDirectory = false
                        });
                    }
                    catch { /* 跳过无权限访问的文件 */ }
                }
            }
            catch { /* 跳过无权限访问的目录 */ }

            if (result.Count % 1000 == 0)
                StatusChanged?.Invoke($"正在遍历目录... 已找到 {result.Count} 个文件");
        }

        sw.Stop();
        StatusChanged?.Invoke($"目录遍历完成！共 {result.Count} 个文件，耗时 {sw.Elapsed.TotalSeconds:F1} 秒");
        return result;
    }
}
