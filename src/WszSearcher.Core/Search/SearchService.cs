using System.Collections.Concurrent;
using System.Diagnostics;
using WszSearcher.Core.ContentSearch;
using WszSearcher.Core.FileNameSearch;
using WszSearcher.Core.Models;

namespace WszSearcher.Core.Search;

/// <summary>
/// 搜索服务——协调文件名搜索（USN Journal）+ 内容搜索（Lucene.NET）
/// 异步搜索聚合、去重排序
/// </summary>
public class SearchService : ISearchService, IDisposable
{
    private readonly FileNameSearchProvider _fileNameSearch;
    private readonly ContentIndexer _contentIndexer;
    private readonly ContentSearcher _contentSearcher;
    private List<string> _indexPaths = [];
    private List<string> _contentExts = [];
    private FileSystemWatcher? _contentWatcher;
    private CancellationTokenSource? _buildCts; // 索引构建取消令牌
    private bool _disposed;

    public SearchService(char driveLetter = 'C')
    {
        _fileNameSearch = new FileNameSearchProvider(driveLetter);
        _fileNameSearch.StateChanged += OnFileNameIndexStateChanged;
        _fileNameSearch.StatusMessage += msg => StatusMessage?.Invoke(msg);
        _fileNameSearch.ProgressChanged += count => ProgressChanged?.Invoke(count);

        _contentIndexer = new ContentIndexer();
        _contentIndexer.StatusChanged += msg => StatusMessage?.Invoke(msg);
        _contentIndexer.ProgressChanged += count => ProgressChanged?.Invoke(count);
        _contentSearcher = new ContentSearcher();
    }

    public event Action<IReadOnlyList<SearchResult>>? ResultsUpdated;
    public event Action<SearchStatus>? StatusChanged;
    public event Action<string>? StatusMessage;
    public event Action<int>? ProgressChanged;

    public SearchStatus Status { get; private set; } = SearchStatus.Idle;

    /// <summary>设置内容索引路径（设置变更后调用）</summary>
    public void SetIndexPaths(List<string> paths)
    {
        if (paths.Count > 0)
        {
            _indexPaths = paths;
            // 从所有路径中提取唯一的盘符
            var drives = paths
                .Where(p => p.Length > 0)
                .Select(p => char.ToUpperInvariant(p[0]))
                .Distinct();
            _fileNameSearch.SetDrives(drives);
            _fileNameSearch.SetFallbackPaths(paths);
        }
        CancelBuild(); // 增删目录时取消正在进行的索引
    }

    private void CancelBuild()
    {
        // 只取消不释放——异步任务还在用此 token，释放会导致 ObjectDisposedException
        _buildCts?.Cancel();
    }

    private void DisposeBuildCts()
    {
        _buildCts?.Dispose();
        _buildCts = null;
    }

    /// <summary>取消正在进行的索引构建（供 UI 按钮调用）</summary>
    public void CancelIndex()
    {
        CancelBuild();
        _fileNameSearch.CancelScan();
        // 立即归零内容索引计数
        _contentIndexer.DocCount = 0;
    }

    /// <summary>设置内容索引的文件后缀（同时用于文件名过滤）</summary>
    public void SetContentExtensions(List<string> extensions)
    {
        _contentExts = extensions ?? [];
        _fileNameSearch.SetExtensionFilter(_contentExts);
    }

    /// <summary>文件名索引文件总数</summary>
    public int FileNameIndexCount => _fileNameSearch.GetIndex().CountInPaths(_indexPaths, _contentExts);

    /// <summary>内容索引文档总数</summary>
    public int ContentIndexCount => _contentIndexer.DocCount;

    /// <summary>
    /// 启动快速初始化：仅扫文件名（秒级），内容索引从磁盘恢复
    /// 用于软件重启后恢复搜索能力，不触发慢速内容重建
    /// </summary>
    public async Task QuickInitAsync()
    {
        Status = SearchStatus.Indexing;
        StatusChanged?.Invoke(Status);

        // 1. 只做文件名扫描（USN，秒级）
        StatusMessage?.Invoke("正在加载文件名索引...");
        await _fileNameSearch.InitializeAsync();

        // 2. 内容索引：磁盘有就加载，没有就跳过（等用户手动重建）
        if (_contentIndexer.IndexExists())
        {
            _contentIndexer.IsReady = true;
            _contentIndexer.SyncDocCount();
            _contentSearcher.RefreshReadyState();
            StatusMessage?.Invoke("内容索引已就绪");
        }
        else
        {
            StatusMessage?.Invoke("内容索引未建立，请在设置中手动重建");
        }

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
    }

    /// <summary>
    /// 初始化：先建文件名索引（USN Journal），再触发内容索引
    /// </summary>
    public async Task InitializeAsync()
    {
        Status = SearchStatus.Indexing;
        StatusChanged?.Invoke(Status);

        // 1. 文件名索引
        StatusMessage?.Invoke("正在建立文件名索引...");
        await _fileNameSearch.InitializeAsync();

        if (_fileNameSearch.State != IndexState.Ready)
        {
            Status = SearchStatus.Ready;
            StatusChanged?.Invoke(Status);
            return;
        }

        // 2. 内容索引
        if (!_contentIndexer.IndexExists() || _contentIndexer.TryGetDocCount() == 0)
        {
            StatusMessage?.Invoke("正在建立内容索引...");
            try
            {
                CancelBuild();
                _buildCts = new CancellationTokenSource();
                await Task.Run(async () => await _contentIndexer.BuildFullIndexAsync(
                    EnumerateFilesFromPaths(_indexPaths), _buildCts.Token), _buildCts.Token);
                _contentSearcher.RefreshReadyState();
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"内容索引建立失败：{ex.Message}");
            }
        }
        else
        {
            _contentIndexer.IsReady = true;
            _contentIndexer.SyncDocCount();
            _contentSearcher.RefreshReadyState();
            StatusMessage?.Invoke("内容索引已就绪");
        }

        _contentIndexer.CloseWriter(); // 释放 Lucene 内存

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StartContentWatcher();
    }

    private bool _rebuilding;
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new();

    private static void Log(string msg) => AppLog.Info("content", msg);

    /// <summary>重建索引：清空文件名索引和内容索引，重新扫描建索引</summary>
    public async Task RebuildIndexAsync()
    {
        _rebuilding = true;
        StopContentWatcher();
        CancelBuild();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;

        Status = SearchStatus.Indexing;
        StatusChanged?.Invoke(Status);

        // 1. 文件名索引
        StatusMessage?.Invoke("正在建立文件名索引...");
        await _fileNameSearch.RebuildAsync();

        // 检查是否被取消
        if (ct.IsCancellationRequested || _fileNameSearch.State != IndexState.Ready)
        {
            FinishWithCancel(ct, "文件名");
            return;
        }

        // 2. 内容索引
        StatusMessage?.Invoke("正在建立内容索引...");
        try
        {
            await Task.Run(async () => await _contentIndexer.BuildFullIndexAsync(
                EnumerateFilesFromPaths(_indexPaths), ct), ct);
            _contentSearcher.RefreshReadyState();
        }
        catch (OperationCanceledException)
        {
            _contentIndexer.DocCount = 0;
            StatusMessage?.Invoke("内容索引已取消");
            _contentIndexer.CloseWriter();
            FinishWithCancel(ct, "内容");
            return;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke($"内容索引重建失败：{ex.Message}");
        }

        _contentIndexer.CloseWriter(); // 释放 Lucene 内存

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StatusMessage?.Invoke($"索引重建完成！文件名 {FileNameIndexCount} 个，内容 {ContentIndexCount} 个");
        _rebuilding = false;
        StartContentWatcher();
        DisposeBuildCts();
    }

    /// <summary>取消后的收尾：重置状态、清理令牌</summary>
    private void FinishWithCancel(CancellationToken ct, string phase)
    {
        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StatusMessage?.Invoke($"{phase}索引已取消");
        _rebuilding = false;
        DisposeBuildCts();
    }

    private void StartContentWatcher()
    {
        if (_indexPaths.Count == 0 || _contentExts.Count == 0) return;
        try
        {
            _contentWatcher?.Dispose();
            _contentWatcher = new FileSystemWatcher
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 32768
            };
            _contentWatcher.Created += OnContentFileChanged;
            _contentWatcher.Changed += OnContentFileChanged;
            _contentWatcher.Deleted += OnContentFileDeleted;
            _contentWatcher.Renamed += OnContentFileRenamed;
            _contentWatcher.Error += (_, e) => Debug.WriteLine($"内容索引监听异常: {e.GetException()?.Message}");

            // 监听所有索引路径
            foreach (var path in _indexPaths)
            {
                if (System.IO.Directory.Exists(path))
                    _contentWatcher.Path = path; // FSW 只能监听一个根，这里需要为每个路径创建...
            }
            // 监听第一个索引路径
            if (_indexPaths.Count > 0 && System.IO.Directory.Exists(_indexPaths[0]))
            {
                _contentWatcher.Path = _indexPaths[0];
                _contentWatcher.EnableRaisingEvents = true;
                Log($"[ContentWatcher] 已启动: {_indexPaths[0]}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"内容索引监听启动失败: {ex.Message}");
        }
    }

    private void StopContentWatcher()
    {
        _contentWatcher?.Dispose();
        _contentWatcher = null;
    }

    private async void OnContentFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding) return;
        var ext = Path.GetExtension(e.FullPath).TrimStart('.').ToLowerInvariant();
        if (!_contentExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;

        // 防抖：同一文件 3 秒内只索引一次
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(e.FullPath, out var last) && (now - last).TotalSeconds < 3)
            return;
        _recentFiles[e.FullPath] = now;

        Log($"[ContentWatcher] 索引: {Path.GetFileName(e.FullPath)}");
        try
        {
            await Task.Delay(200);
            await _contentIndexer.IndexFileAsync(e.FullPath);
            _contentIndexer.CommitChanges();
            Log($"增量索引完成: {Path.GetFileName(e.FullPath)}, 文件名={_fileNameSearch.IndexCount}, 内容={_contentIndexer.DocCount}");
        }
        catch (Exception ex) { Log($"增量索引失败: {ex.Message}"); }
    }

    private void OnContentFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding) return;
        Log($"[ContentWatcher] 删除: {Path.GetFileName(e.FullPath)}");
        try
        {
            _contentIndexer.RemoveFile(e.FullPath);
            Log($"删除索引完成: {Path.GetFileName(e.FullPath)}, 内容={_contentIndexer.DocCount}");
        }
        catch (Exception ex) { Log($"删除索引失败: {ex.Message}"); }
    }

    private void OnContentFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_rebuilding) return;
        try
        {
            _contentIndexer.RemoveFile(e.OldFullPath);
            var ext = Path.GetExtension(e.FullPath).TrimStart('.').ToLowerInvariant();
            if (_contentExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                _ = _contentIndexer.IndexFileAsync(e.FullPath);
        }
        catch (Exception ex) { Debug.WriteLine($"重命名索引失败: {ex.Message}"); }
    }

    /// <summary>异步搜索：并行搜索文件名和内容，合并去重。</summary>
    public async Task SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsUpdated?.Invoke([]);
            return;
        }

        Status = SearchStatus.Searching;
        StatusChanged?.Invoke(Status);

        try
        {
            // 包装为 Task.Run 实现真正的异步，不阻塞 UI
            var results = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var fileNameResults = _fileNameSearch.Search(query, _indexPaths, maxResults: 30);
                var contentResults = _contentSearcher?.Search(query, maxResults: 20) ?? [];

                // 按配置的后缀过滤内容搜索结果
                var validExts = new HashSet<string>(_contentExts, StringComparer.OrdinalIgnoreCase);
                contentResults = contentResults
                    .Where(r => validExts.Contains(
                        Path.GetExtension(r.FullPath).TrimStart('.')))
                    .ToList();

                // HashSet 去重
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<SearchResult>();

                foreach (var r in fileNameResults)
                {
                    if (seen.Add(r.FullPath))
                        merged.Add(r);
                }
                foreach (var r in contentResults)
                {
                    if (seen.Add(r.FullPath))
                        merged.Add(r);
                }

                return (IReadOnlyList<SearchResult>)merged;
            }, ct);

            ResultsUpdated?.Invoke(results);
        }
        catch (OperationCanceledException)
        {
            // 被新搜索取消，静默
        }
        finally
        {
            Status = SearchStatus.Ready;
            StatusChanged?.Invoke(Status);
        }
    }

    /// <summary>从指定路径列表遍历文件（内容索引输入源），跳过重解析点和系统目录</summary>
    private IEnumerable<string> EnumerateFilesFromPaths(List<string> rootPaths)
    {
        AppLog.Info("content", $"枚举开始: 路径={rootPaths.Count}, 后缀=[{string.Join(",", _contentExts)}]");
        var textExts = new HashSet<string>(_contentExts, StringComparer.OrdinalIgnoreCase);

        var dirEnumOptions = new System.IO.EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        var dirs = new Queue<string>();
        const int maxDirs = 50_000;
        const int maxFiles = 100_000;
        var fileCount = 0;

        foreach (var root in rootPaths)
        {
            if (!System.IO.Directory.Exists(root)) continue;
            dirs.Enqueue(root);
        }

        while (dirs.Count > 0 && dirs.Count <= maxDirs && fileCount < maxFiles)
        {
            var dir = dirs.Dequeue();

            string[] subDirs;
            try { subDirs = System.IO.Directory.GetDirectories(dir, "*", dirEnumOptions); }
            catch (UnauthorizedAccessException) { continue; }
            catch (System.IO.DirectoryNotFoundException) { continue; }
            catch (System.IO.PathTooLongException) { continue; }
            catch (System.IO.IOException) { continue; }

            foreach (var subDir in subDirs)
            {
                var dirName = System.IO.Path.GetFileName(subDir);
                if (dirName.Length > 0 && dirName[0] == '.') continue;
                if (dirName is "node_modules" or ".git" or "bin" or "obj" or "packages"
                    or "vendor" or "__pycache__" or "target" or "build" or "dist"
                    or "bower_components" or ".vs" or ".vscode" or ".idea")
                    continue;
                dirs.Enqueue(subDir);
            }

            string[] files;
            try { files = System.IO.Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (System.IO.DirectoryNotFoundException) { continue; }
            catch (System.IO.IOException) { continue; }

            foreach (var file in files)
            {
                if (fileCount >= maxFiles) yield break;
                var ext = System.IO.Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                if (textExts.Contains(ext))
                {
                    fileCount++;
                    yield return file;
                }
            }
        }
    }

    private void OnFileNameIndexStateChanged(IndexState state)
    {
        // 由文件名索引状态驱动全局状态
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopContentWatcher();
        _fileNameSearch.Dispose();
        _contentIndexer.Dispose();
        _contentSearcher.Dispose();
    }
}
