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
    public async Task<List<FileRecord>> ScanAllAsync(List<string> fallbackPaths)
    {
        return await Task.Run(() => ScanAll(fallbackPaths));
    }

    private List<FileRecord> ScanAll(List<string> fallbackPaths)
    {
        var result = new List<FileRecord>();
        var sw = Stopwatch.StartNew();

        AppLog.Info("fname", $"USN 尝试打开卷: {_volume}");
        using var volumeHandle = UsnApi.OpenVolume(_volume);
        if (volumeHandle.IsInvalid)
        {
            AppLog.Warn("fname", $"USN 无法打开卷 {_volume}（需要管理员权限）");
            StatusChanged?.Invoke($"无法访问卷 {_volume}，将使用目录遍历方式（需要管理员权限运行以获得最大速度）");
            return FallbackScan(fallbackPaths);
        }

        AppLog.Info("fname", "USN 查询 Journal 信息...");
        if (!UsnApi.QueryUsnJournal(volumeHandle, out var journalData))
        {
            AppLog.Warn("fname", "USN Journal 不可用");
            StatusChanged?.Invoke("USN Journal 不可用，使用目录遍历方式");
            return FallbackScan(fallbackPaths);
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
        AppLog.Info("fname", $"USN 枚举完成: {count} 条记录");

        if (count == 0)
        {
            AppLog.Warn("fname", "USN 枚举为 0，降级为目录遍历");
            return FallbackScan(fallbackPaths);
        }

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
                    NamePinyin = Analysis.PinyinHelper.GetFirstLetters(record.FileName),
                    NameFullPinyin = Analysis.PinyinHelper.GetPinyin(record.FileName),
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

    /// <summary>备用方案：从指定路径列表遍历（非管理员模式）</summary>
	    public List<FileRecord> FallbackScan(List<string> rootPaths)
	    {
	        AppLog.Info("fname", $"Fallback 开始: {rootPaths.Count} 个路径 [{string.Join(", ", rootPaths)}]");
	        var result = new List<FileRecord>();
	        var sw = Stopwatch.StartNew();

	        foreach (var root in rootPaths)
	        {
	            if (!System.IO.Directory.Exists(root))
	            {
	                AppLog.Warn("fname", $"Fallback 路径不存在: {root}");
	                continue;
	            }
	            AppLog.Info("fname", $"Fallback 遍历: {root}");
	            var dirs = new Queue<string>();
	            dirs.Enqueue(root);

                while (dirs.Count > 0)
                {
                    var dir = dirs.Dequeue();
                    try
                    {
                        var subDirCount = 0;
                foreach (var subDir in System.IO.Directory.EnumerateDirectories(dir, "*", new System.IO.EnumerationOptions
                    { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }))
                {
                    var dn = Path.GetFileName(subDir);
                    if (dn.Length > 0 && dn[0] != '.' && dn is not "node_modules" and not ".git")
                        dirs.Enqueue(subDir);
                    subDirCount++;
                }

                var fileCount = 0;
                foreach (var file in System.IO.Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        result.Add(new FileRecord
                        {
                            FileName = fi.Name,
                            FullPath = fi.FullName,
                            Directory = fi.DirectoryName ?? "",
                            NamePinyin = Analysis.PinyinHelper.GetFirstLetters(fi.Name),
                            NameFullPinyin = Analysis.PinyinHelper.GetPinyin(fi.Name),
                            FileSize = fi.Length,
                            LastModified = fi.LastWriteTime,
                            IsDirectory = false
                        });
                        fileCount++;
                    }
                    catch { }
                }
                if (subDirCount > 0 || fileCount > 0) { } // 只计不输出
            }
            catch (Exception ex) { AppLog.Warn("fname", $"  遍历失败 {dir}: {ex.Message}"); }
        }
    }

    sw.Stop();
    AppLog.Info("fname", $"Fallback 完成: {result.Count} 个文件");
    StatusChanged?.Invoke($"目录遍历完成！共 {result.Count} 个文件，耗时 {sw.Elapsed.TotalSeconds:F1} 秒");
        return result;
    }
}
