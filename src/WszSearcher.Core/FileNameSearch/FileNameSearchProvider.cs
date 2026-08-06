using WszSearcher.Core.Localization;
using WszSearcher.Core.Models;

namespace WszSearcher.Core.FileNameSearch;

/// <summary>
/// 文件名搜索提供者——包装 USN 扫描 + 内存索引 + 文件监听
/// 支持多盘符，对外提供统一的文件名搜索接口
/// </summary>
public class FileNameSearchProvider : IDisposable
{
    private readonly FileNameIndex _index = new();
    private List<char> _drives = []; // 多盘符支持

    /// <summary>当前扫描驱动器列表</summary>
    public IReadOnlyList<char> Drives => _drives;

    /// <summary>更新扫描驱动器</summary>
    private List<string> _fallbackPaths = []; // Fallback 遍历路径

    /// <summary>设置扫描驱动器（清空旧列表后设置）</summary>
    public void SetDrives(IEnumerable<char> drives)
    {
        _drives = drives.Distinct().ToList();
    }

    /// <summary>设置 Fallback 遍历路径（USN 不可用时使用）</summary>
    public void SetFallbackPaths(List<string> paths)
    {
        _fallbackPaths = paths;
    }
    private List<FileSystemWatcher> _watchers = []; // 每个盘符一个 FSW
    private int _initialized; // 使用 int + Interlocked 防止 TOCTOU
    private bool _disposed;
    private volatile bool _rebuilding;
    private CancellationTokenSource? _scanCts; // 扫描取消令牌
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _recentFiles = new();
    private HashSet<string> _extFilter = []; // 文件名后缀过滤
    private List<string> _excludePatterns = []; // 用户排除目录模式（*\node_modules 等），扫描与 watcher 共用

    /// <summary>索引状态变更事件</summary>
    public event Action<IndexState>? StateChanged;
    /// <summary>索引进度</summary>
    public event Action<int>? ProgressChanged;
    /// <summary>索引状态消息（携带资源 key 与参数，由 UI 层翻译显示）</summary>
    public event Action<StatusMessage>? StatusMessage;

    public IndexState State { get; private set; } = IndexState.NotInitialized;

    /// <summary>获取内部索引</summary>
    public FileNameIndex GetIndex() => _index;

    /// <summary>设置文件名后缀过滤（空列表=不过滤）</summary>
    public void SetExtensionFilter(List<string> extensions)
    {
        _extFilter = extensions.Count > 0
            ? new HashSet<string>(extensions.Select(e => $".{e.TrimStart('.')}"), StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>设置排除目录模式（*\node_modules 等），扫描与 watcher 事件共用过滤</summary>
    public void SetExcludePaths(List<string> patterns)
    {
        _excludePatterns = patterns ?? [];
    }

    /// <summary>当前索引文件总数</summary>
    public int IndexCount => _index.Count;

    public FileNameSearchProvider(char driveLetter = 'C')
    {
        _drives = [driveLetter];
    }
    /// <summary>取消正在进行的扫描</summary>
    public void CancelScan()
    {
        _scanCts?.Cancel();
    }

    public async Task InitializeAsync()
    {
        // 原子性 Check-and-Set 防止并发双重初始化
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        State = IndexState.Scanning;
        InvokeStateChanged(State);

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var allFiles = new List<FileRecord>();

        foreach (var drive in _drives)
        {
            ct.ThrowIfCancellationRequested();

            // 过滤出属于当前盘符的 fallback 路径
            var drivePaths = _fallbackPaths
                .Where(p => p.Length > 0 && char.ToUpperInvariant(p[0]) == char.ToUpperInvariant(drive))
                .ToList();

            var scanner = new UsnFileScanner(drive) { CancellationToken = ct };
            scanner.ProgressChanged += count => InvokeProgressChanged(count);
            scanner.StatusChanged += msg => InvokeStatusMessage(msg);

            try
            {
                var files = await scanner.ScanAllAsync(drivePaths.Count > 0 ? drivePaths : _fallbackPaths);
                AppLog.Info("fname", $"盘符 {drive}: 扫描完成 {files.Count} 个文件");
                allFiles.AddRange(files);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Warn("fname", $"盘符 {drive}: 扫描失败 - {ex.Message}");
            }
        }

        AppLog.Info("fname", $"全部扫描完成: {allFiles.Count} 个文件 (盘符=[{string.Join(",", _drives)}])");

        try
        {
            // 后缀过滤 + 排除目录过滤（黑名单/点开头/用户 ExcludePaths，与内容索引共用规则）
            var filtered = allFiles.Where(f =>
                (_extFilter.Count == 0 || PassExtFilter(f.FullPath)) &&
                !PathFilter.IsExcluded(f.FullPath, _excludePatterns)).ToList();
            _index.AddRange(filtered);

            // 启动文件变更监听
            StartWatchers();

            State = IndexState.Ready;
            InvokeStatusMessage(new StatusMessage(StatusKeys.FileNameIndexReady, _index.Count));
        }
        catch (OperationCanceledException)
        {
            State = IndexState.Error; // 取消不视为 Ready，防止继续进入内容索引
            InvokeStatusMessage(new StatusMessage(StatusKeys.FileNameScanCancelled));
            InvokeStateChanged(State);
            return; // 不继续后续流程
        }
        catch (Exception ex)
        {
            State = IndexState.Error;
            InvokeStatusMessage(new StatusMessage(StatusKeys.FileNameIndexFailed, ex.Message));
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

    private void InvokeStatusMessage(StatusMessage msg)
    {
        var handler = StatusMessage;
        handler?.Invoke(msg);
    }

    /// <summary>重建索引：清空后重新扫描</summary>
    public async Task RebuildAsync()
    {
        _rebuilding = true; // 标记重建中，防止 FileSystemWatcher 事件修改索引

        // 停止所有旧的 FileSystemWatcher
        StopWatchers();

        _index.Clear();
        Interlocked.Exchange(ref _initialized, 0); // 原子重置初始化标记，允许重新初始化
        await InitializeAsync();

        _rebuilding = false;
    }

    /// <summary>搜索文件名</summary>
    public List<SearchResult> Search(string query, IReadOnlyList<string>? paths = null, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || State != IndexState.Ready)
            return [];

        var files = _index.Search(query, paths ?? Array.Empty<string>(), maxResults);
        var results = files.Select(f => new SearchResult
            {
                FileName = f.FileName,
                FullPath = f.FullPath,
                FileSize = f.FileSize,
                LastModified = f.LastModified,
                ResultType = SearchResultType.FileName,
                Score = CalculateScore(query, f.FileName)
            }).ToList();

        // 对未获取到文件大小的结果做懒加载（USN V2 记录无 FileSize，仅对搜索结果生效）
        foreach (var r in results)
        {
            if (r.FileSize == 0)
            {
                try { r.FileSize = new System.IO.FileInfo(r.FullPath).Length; } catch { }
            }
        }
        return results;
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

    /// <summary>启动所有盘符的 FileSystemWatcher 监听文件变更</summary>
    private void StartWatchers()
    {
        StopWatchers();

        foreach (var drive in _drives)
        {
            try
            {
                var watcher = new FileSystemWatcher($@"{drive}:\")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 65536
                };

                watcher.Created += OnFileCreated;
                watcher.Deleted += OnFileDeleted;
                watcher.Renamed += OnFileRenamed;
                watcher.Changed += OnFileChanged;
                watcher.Error += OnWatcherError;

                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                InvokeStatusMessage(new StatusMessage(StatusKeys.FileWatcherStartFailed, drive, ex.Message));
            }
        }
    }

    /// <summary>停止所有盘符的 FileSystemWatcher</summary>
    private void StopWatchers()
    {
        foreach (var w in _watchers)
        {
            try { w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding || ShouldIgnore(e.FullPath) || !PassExtFilter(e.FullPath)) return;
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
                    NamePinyin = Analysis.PinyinHelper.GetFirstLetters(fi.Name),
                    NameFullPinyin = Analysis.PinyinHelper.GetPinyin(fi.Name),
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
                    NamePinyin = Analysis.PinyinHelper.GetFirstLetters(fi.Name),
                    NameFullPinyin = Analysis.PinyinHelper.GetPinyin(fi.Name),
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
                    NamePinyin = Analysis.PinyinHelper.GetFirstLetters(fi.Name),
                    NameFullPinyin = Analysis.PinyinHelper.GetPinyin(fi.Name),
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

    /// <summary>是否应被文件名索引忽略（临时文件 / 排除目录 / Lucene 索引自身文件）</summary>
    private bool ShouldIgnore(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.Length == 0) return true;
        if (name[0] == '~' || (name[0] == '$' && name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (path.Contains("\\Index\\", StringComparison.OrdinalIgnoreCase) &&
            (name.StartsWith('_') || name.StartsWith("segments") || name == "write.lock"))
            return true;
        // 黑名单/点开头/用户 ExcludePaths 目录：与内容索引共用同一过滤规则
        if (PathFilter.IsExcluded(path, _excludePatterns)) return true;
        return false;
    }

    /// <summary>是否通过后缀过滤（空过滤器=全部通过）</summary>
    private bool PassExtFilter(string path)
    {
        if (_extFilter.Count == 0) return true;
        var ext = System.IO.Path.GetExtension(path);
        return _extFilter.Contains(ext);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        InvokeStatusMessage(new StatusMessage(StatusKeys.FileWatcherError, e.GetException()?.Message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatchers();
        _scanCts?.Dispose();
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
