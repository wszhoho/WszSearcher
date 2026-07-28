using WszSearcher.Core.Models;

namespace WszSearcher.Core.FileNameSearch;

/// <summary>
/// 文件名搜索提供者——包装 USN 扫描 + 内存索引 + 文件监听
/// 对外提供统一的文件名搜索接口
/// </summary>
public class FileNameSearchProvider : IDisposable
{
    private readonly FileNameIndex _index = new();
    private char _driveLetter;

    /// <summary>当前扫描驱动器</summary>
    public char DriveLetter => _driveLetter;

    /// <summary>更新扫描驱动器</summary>
    public void SetDrive(char driveLetter)
    {
        if (_driveLetter == driveLetter) return;
        _driveLetter = driveLetter;
    }
    private FileSystemWatcher? _watcher;
    private int _initialized; // 使用 int + Interlocked 防止 TOCTOU
    private bool _disposed;
    private volatile bool _rebuilding;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentFiles = new(); // 防抖

    /// <summary>索引状态变更事件</summary>
    public event Action<IndexState>? StateChanged;
    /// <summary>索引进度</summary>
    public event Action<int>? ProgressChanged;
    /// <summary>索引状态消息</summary>
    public event Action<string>? StatusMessage;

    public IndexState State { get; private set; } = IndexState.NotInitialized;

    /// <summary>获取内部索引（供过滤统计等用途）</summary>
    public FileNameIndex GetIndex() => _index;

    /// <summary>当前索引文件总数</summary>
    public int IndexCount => _index.Count;

    public FileNameSearchProvider(char driveLetter = 'C')
    {
        _driveLetter = driveLetter;
    }
    public async Task InitializeAsync()
    {
        // 原子性 Check-and-Set 防止并发双重初始化
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        State = IndexState.Scanning;
        InvokeStateChanged(State);

        var scanner = new UsnFileScanner(_driveLetter);
        scanner.ProgressChanged += count => InvokeProgressChanged(count);
        scanner.StatusChanged += msg => InvokeStatusMessage(msg);

        try
        {
            var files = await scanner.ScanAllAsync();

            _index.AddRange(files);

            // 启动文件变更监听
            StartWatcher();

            State = IndexState.Ready;
            InvokeStatusMessage($"索引就绪，共 {_index.Count} 个文件");
        }
        catch (OperationCanceledException)
        {
            State = IndexState.Ready;
            InvokeStatusMessage("索引已取消");
        }
        catch (Exception ex)
        {
            State = IndexState.Error;
            InvokeStatusMessage($"索引失败：{ex.Message}");
        }

        InvokeStateChanged(State);
    }

    // 线程安全的事件触发辅助方法（缓存委托副本防止 null 传播竞态）
    private void InvokeStateChanged(IndexState state)
    {
        var handler = StateChanged;
        handler?.Invoke(state);
    }

    private void InvokeProgressChanged(int count)
    {
        var handler = ProgressChanged;
        handler?.Invoke(count);
    }

    private void InvokeStatusMessage(string msg)
    {
        var handler = StatusMessage;
        handler?.Invoke(msg);
    }

    /// <summary>重建索引：清空后重新扫描</summary>
    public async Task RebuildAsync()
    {
        _rebuilding = true; // 标记重建中，防止 FileSystemWatcher 事件修改索引

        // 停止旧的 FileSystemWatcher
        _watcher?.Dispose();
        _watcher = null;

        _index.Clear();
        Interlocked.Exchange(ref _initialized, 0); // 原子重置初始化标记，允许重新初始化
        await InitializeAsync();

        _rebuilding = false;
    }

    /// <summary>搜索文件名</summary>
    public List<SearchResult> Search(string query, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || State != IndexState.Ready)
            return [];

        var files = _index.Search(query, maxResults);
        return files.Select(f => new SearchResult
        {
            FileName = f.FileName,
            FullPath = f.FullPath,
            Directory = f.Directory,
            FileSize = f.FileSize,
            LastModified = f.LastModified,
            ResultType = SearchResultType.FileName,
            Score = CalculateScore(query, f.FileName)
        }).ToList();
    }

    /// <summary>计算匹配分数（前缀 > 包含 > 路径）</summary>
    private static double CalculateScore(string query, string fileName)
    {
        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1.0 - (fileName.Length - query.Length) * 0.001; // 越短的前缀匹配分越高

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.7;

        return 0.5;
    }

    /// <summary>启动 FileSystemWatcher 监听文件变更</summary>
    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher($@"{_driveLetter}:\")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 65536
            };

            _watcher.Created += OnFileCreated;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Changed += OnFileChanged;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            InvokeStatusMessage($"文件监听启动失败（不影响搜索）：{ex.Message}");
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding || ShouldIgnore(e.FullPath)) return;
        if (!Debounce(e.FullPath)) return;
        try
        {
            var fi = new FileInfo(e.FullPath);
            if (fi.Exists && !fi.Attributes.HasFlag(FileAttributes.Directory))
            {
                _index.AddOrUpdate(new FileRecord
                {
                    FileName = fi.Name,
                    FullPath = fi.FullName,
                    Directory = fi.DirectoryName ?? "",
                    FileSize = fi.Length,
                    LastModified = fi.LastWriteTime
                });
                AppLog.Info("fname", $"+ {fi.Name}");
            }
        }
        catch { }
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding || ShouldIgnore(e.FullPath)) return;
        _index.Remove(e.FullPath);
        AppLog.Info("fname", $"- {System.IO.Path.GetFileName(e.FullPath)}");
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_rebuilding || ShouldIgnore(e.FullPath)) return;
        if (!Debounce(e.FullPath)) return;
        _index.Remove(e.OldFullPath);
        try
        {
            var fi = new FileInfo(e.FullPath);
            if (fi.Exists && !fi.Attributes.HasFlag(FileAttributes.Directory))
            {
                _index.AddOrUpdate(new FileRecord
                {
                    FileName = fi.Name,
                    FullPath = fi.FullName,
                    Directory = fi.DirectoryName ?? "",
                    FileSize = fi.Length,
                    LastModified = fi.LastWriteTime
                });
            }
        }
        catch { }
        AppLog.Info("fname", $"改名: {System.IO.Path.GetFileName(e.OldFullPath)} -> {System.IO.Path.GetFileName(e.FullPath)}");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding || ShouldIgnore(e.FullPath)) return;
        if (!Debounce(e.FullPath)) return;
        try
        {
            var fi = new FileInfo(e.FullPath);
            if (fi.Exists && !fi.Attributes.HasFlag(FileAttributes.Directory))
            {
                _index.AddOrUpdate(new FileRecord
                {
                    FileName = fi.Name,
                    FullPath = fi.FullName,
                    Directory = fi.DirectoryName ?? "",
                    FileSize = fi.Length,
                    LastModified = fi.LastWriteTime
                });
            }
        }
        catch { }
    }

    /// <summary>防抖：同一文件 2 秒内不重复处理</summary>
    private bool Debounce(string path)
    {
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(path, out var last) && (now - last).TotalSeconds < 2)
            return false;
        _recentFiles[path] = now;
        return true;
    }

    /// <summary>是否应被文件名索引忽略</summary>
    private static bool ShouldIgnore(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.Length == 0) return true;
        // Office 临时文件
        if (name[0] == '~' || (name[0] == '$' && name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)))
            return true;
        // Lucene 索引目录
        if (path.Contains("\\Index\\", StringComparison.OrdinalIgnoreCase) &&
            (name.StartsWith('_') || name.StartsWith("segments") || name == "write.lock"))
            return true;
        return false;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        InvokeStatusMessage($"文件监听异常：{e.GetException()?.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _index.Dispose(); // 释放 ReaderWriterLockSlim
    }
}

public enum IndexState
{
    NotInitialized,
    Scanning,
    Ready,
    Error
}
