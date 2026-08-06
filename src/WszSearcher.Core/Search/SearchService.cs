using System.Collections.Concurrent;
using System.Diagnostics;
using WszSearcher.Core.ContentSearch;
using WszSearcher.Core.FileNameSearch;
using WszSearcher.Core.Localization;
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
    private List<string> _excludePatterns = []; // 用户排除目录模式（*\node_modules 等），watcher 事件过滤用
    private readonly List<FileSystemWatcher> _contentWatchers = []; // 每个索引路径一个 watcher（FSW 只能监听一个根）
    private CancellationTokenSource? _buildCts; // 索引构建取消令牌
    private long _lastWatcherRebuildTicks; // watcher 重建节流时间戳（防事件风暴下无限重建）
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
    public event Action<StatusMessage>? StatusMessage;
    public event Action<int>? ProgressChanged;
    public event Action? IndexUpdated; // 实时索引更新完成后触发（供 UI 自动刷新结果）

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
        _backfillCts?.Cancel(); // 路径变更后取消后台补齐（补齐基于旧路径，路径变了无意义）
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
        _backfillCts?.Cancel(); // 取消后台补齐
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

    /// <summary>设置排除目录模式（*\node_modules 等），watcher 事件与扫描共用过滤</summary>
    public void SetExcludePaths(List<string> patterns)
    {
        _excludePatterns = patterns ?? [];
        _fileNameSearch.SetExcludePaths(_excludePatterns); // 文件名搜索同步应用同一屏蔽规则
    }

    /// <summary>
    /// watcher 事件统一过滤：命中黑名单/排除目录、无索引后缀、点开头目录 → false。
    /// 必须在进入索引流程前调用，避免 C 盘全盘事件风暴拖垮软件
    /// </summary>
    private bool IsIndexablePath(string path)
    {
        // 1. 后缀过滤（目录无后缀自然被排除）
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (!_contentExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;

        // 2. 路径排除过滤（黑名单/点开头/用户 ExcludePaths，与文件名搜索共用同一规则）
        return !PathFilter.IsExcluded(path, _excludePatterns);
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
        StatusMessage?.Invoke(new StatusMessage(StatusKeys.LoadingFileNameIndex));
        await _fileNameSearch.InitializeAsync();

        // 2. 内容索引：磁盘有就加载，没有就跳过（等用户手动重建）
        if (_contentIndexer.IndexExists())
        {
            _contentIndexer.IsReady = true;
            _contentIndexer.SyncDocCount();
            _contentSearcher.RefreshReadyState();
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexReady));
        }
        else
        {
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexNotBuilt));
        }

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);

        // 启动内容 watcher 恢复实时更新 + 后台补齐缺失索引（索引存在时才有意义，否则等用户手动重建）
        if (_contentIndexer.IndexExists())
        {
            StartContentWatcher();
            _ = BackfillMissingAsync();
        }
    }

    /// <summary>
    /// 启动后后台补齐缺失的内容索引：对比磁盘文件与索引已有文档，只索引缺失部分。
    /// 解决历史文件在 watcher 未启动期间未被索引、只能重建才能搜到的问题；保留现有索引，不打断搜索
    /// </summary>
    private async Task BackfillMissingAsync()
    {
        if (_rebuilding || _backfillCts is not null) return; // 防重入
        _backfillCts = new CancellationTokenSource();
        var ct = _backfillCts.Token;
        try
        {
            // 1. 索引中已有的路径集合
            var indexed = _contentIndexer.GetIndexedPaths();
            ct.ThrowIfCancellationRequested();

            // 2. 枚举磁盘并求差集（磁盘有、索引无）
            var missing = new List<string>();
            foreach (var f in EnumerateFilesFromPaths(_indexPaths))
            {
                ct.ThrowIfCancellationRequested();
                if (!indexed.Contains(f)) missing.Add(f);
            }

            if (missing.Count == 0)
            {
                StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexCompleteNoBackfill));
                return;
            }

            // 3. 逐文件补齐（IndexFileInternal 内含 File.Exists 防复活校验）
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.BackfillingMissingIndex, missing.Count));
            foreach (var f in missing)
            {
                ct.ThrowIfCancellationRequested();
                await _contentIndexer.IndexFileAsync(f, ct);
            }
            _contentIndexer.CommitChanges();
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.BackfillComplete, missing.Count));
            IndexUpdated?.Invoke(); // 通知 UI 刷新当前搜索结果
        }
        catch (OperationCanceledException)
        {
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.BackfillCancelled));
        }
        catch (Exception ex)
        {
            Log($"内容索引补齐失败: {ex.Message}");
        }
        finally
        {
            _backfillCts?.Dispose();
            _backfillCts = null;
        }
    }

    /// <summary>
    /// 初始化：先建文件名索引（USN Journal），再触发内容索引
    /// </summary>
    public async Task InitializeAsync()
    {
        Status = SearchStatus.Indexing;
        StatusChanged?.Invoke(Status);

        // 1. 文件名索引
        StatusMessage?.Invoke(new StatusMessage(StatusKeys.BuildingFileNameIndex));
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
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.BuildingContentIndex));
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
                StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexBuildFailed, ex.Message));
            }
        }
        else
        {
            _contentIndexer.IsReady = true;
            _contentIndexer.SyncDocCount();
            _contentSearcher.RefreshReadyState();
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexReady));
        }

        _contentIndexer.CloseWriter(); // 释放 Lucene 内存

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StartContentWatcher();
    }

    private bool _rebuilding;
    private CancellationTokenSource? _backfillCts; // 启动补齐缺失索引的取消令牌
    private readonly ConcurrentDictionary<string, DateTime> _recentFiles = new();

    private static void Log(string msg) => AppLog.Info("content", msg);

    /// <summary>重建索引：清空文件名索引和内容索引，重新扫描建索引</summary>
    public async Task RebuildIndexAsync()
    {
        _rebuilding = true;
        StopContentWatcher();
        _backfillCts?.Cancel(); // 取消后台补齐（重建会全量索引，无需补齐）
        CancelBuild();
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;

        Status = SearchStatus.Indexing;
        StatusChanged?.Invoke(Status);

        // 1. 文件名索引
        StatusMessage?.Invoke(new StatusMessage(StatusKeys.BuildingFileNameIndex));
        try
        {
            await _fileNameSearch.RebuildAsync();
        }
        catch (OperationCanceledException)
        {
            FinishWithCancel(ct, StatusKeys.IndexCancelledFileName);
            return;
        }

        // 检查是否被取消（RebuildAsync 正常完成但状态未就绪的情况）
        if (ct.IsCancellationRequested || _fileNameSearch.State != IndexState.Ready)
        {
            FinishWithCancel(ct, StatusKeys.IndexCancelledFileName);
            return;
        }

        // 2. 内容索引
        StatusMessage?.Invoke(new StatusMessage(StatusKeys.BuildingContentIndex));
        try
        {
            await Task.Run(async () => await _contentIndexer.BuildFullIndexAsync(
                EnumerateFilesFromPaths(_indexPaths), ct), ct);
            _contentSearcher.RefreshReadyState();
        }
        catch (OperationCanceledException)
        {
            _contentIndexer.DocCount = 0;
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexCancelled));
            _contentIndexer.CloseWriter();
            FinishWithCancel(ct, StatusKeys.IndexCancelledContent);
            return;
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(new StatusMessage(StatusKeys.ContentIndexRebuildFailed, ex.Message));
        }

        _contentIndexer.CloseWriter(); // 释放 Lucene 内存

        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StatusMessage?.Invoke(new StatusMessage(StatusKeys.RebuildComplete, FileNameIndexCount, ContentIndexCount));
        _rebuilding = false;
        StartContentWatcher();
        DisposeBuildCts();
    }

    /// <summary>取消后的收尾：重置状态、清理令牌</summary>
    private void FinishWithCancel(CancellationToken ct, string statusKey)
    {
        Status = SearchStatus.Ready;
        StatusChanged?.Invoke(Status);
        StatusMessage?.Invoke(new StatusMessage(statusKey));
        _rebuilding = false;
        DisposeBuildCts();
    }

    private void StartContentWatcher()
    {
        if (_indexPaths.Count == 0 || _contentExts.Count == 0) return;
        StopContentWatcher();
        try
        {
            // 每个索引路径各建一个 watcher（FileSystemWatcher 只能监听一个根目录）
            foreach (var path in _indexPaths)
            {
                if (!System.IO.Directory.Exists(path)) continue;
                var watcher = new FileSystemWatcher
                {
                    Path = path,
                    IncludeSubdirectories = true,
                    // 去掉 Size：LastWrite 已能感知内容变更，Size 会让写入中的大文件高频触发
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024 // FSW 上限，降低高并发事件下缓冲溢出概率
                };
                watcher.Created += OnContentFileChanged;
                watcher.Changed += OnContentFileChanged;
                watcher.Deleted += OnContentFileDeleted;
                watcher.Renamed += OnContentFileRenamed;
                watcher.Error += (_, _) => RebuildContentWatcher(path); // 缓冲溢出后自动重建，防静默失效
                watcher.EnableRaisingEvents = true;
                _contentWatchers.Add(watcher);
                Log($"[ContentWatcher] 已启动: {path}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"内容索引监听启动失败: {ex.Message}");
        }
    }

    private void StopContentWatcher()
    {
        foreach (var watcher in _contentWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _contentWatchers.Clear();
    }

    /// <summary>watcher 缓冲溢出/异常后自动重建监听，5 秒节流防止事件风暴下无限重建</summary>
    private void RebuildContentWatcher(string path)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWatcherRebuildTicks);
        if (now - last < TimeSpan.FromSeconds(5).Ticks) return;
        Interlocked.Exchange(ref _lastWatcherRebuildTicks, now);
        Log($"[ContentWatcher] 监听异常，自动重建: {path}");
        _ = Task.Run(() =>
        {
            if (_rebuilding) return;
            try { StopContentWatcher(); StartContentWatcher(); }
            catch (Exception ex) { Debug.WriteLine($"内容索引监听重建失败: {ex.Message}"); }
        });
    }

    private async void OnContentFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding) return;
        // 统一过滤：黑名单/排除目录、无索引后缀、点开头目录直接丢弃（事件已触发，但不再进索引流程）
        if (!IsIndexablePath(e.FullPath)) return;

        // 防抖：同一文件 3 秒内只索引一次
        var now = DateTime.UtcNow;
        if (_recentFiles.TryGetValue(e.FullPath, out var last) && (now - last).TotalSeconds < 3)
            return;
        // 防抖字典上限清理：超过 2 万条时剔除 3 秒前的旧条目，防 C 盘高频事件下内存膨胀
        if (_recentFiles.Count > 20_000)
        {
            var stale = _recentFiles.Where(kv => (now - kv.Value).TotalSeconds > 3).Select(kv => kv.Key).ToList();
            foreach (var key in stale) _recentFiles.TryRemove(key, out _);
        }
        _recentFiles[e.FullPath] = now;

        Log($"[ContentWatcher] 索引: {Path.GetFileName(e.FullPath)}");
        try
        {
            // 等待文件写入完成（大文件粘贴时避免索引到半截内容）；文件被删除则直接放弃
            await WaitForFileStable(e.FullPath);
            await _contentIndexer.IndexFileAsync(e.FullPath);
            _contentIndexer.CommitChanges();
            Log($"增量索引完成: {Path.GetFileName(e.FullPath)}, 文件名={_fileNameSearch.IndexCount}, 内容={_contentIndexer.DocCount}");
            IndexUpdated?.Invoke(); // 通知 UI 刷新当前搜索结果
        }
        catch (Exception ex) { Log($"增量索引失败: {ex.Message}"); }
    }

    /// <summary>
    /// 等待文件大小稳定（写入完成）：两次采样间隔内大小不再变化视为稳定，最长等待 timeoutMs。
    /// 文件被删除或读取失败时提前返回，交给后续事件或防复活逻辑处理
    /// </summary>
    private static async Task WaitForFileStable(string path, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        long lastLen = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!File.Exists(path)) return; // 文件已被删除，放弃索引
            long len;
            try { len = new FileInfo(path).Length; }
            catch { return; } // 被其他进程独占读取失败，交给后续 Changed 事件重试
            if (len == lastLen)
            {
                await Task.Delay(300); // 大小稳定后再等 300ms 确认写入收尾
                return;
            }
            lastLen = len;
            await Task.Delay(300);
        }
    }

    private void OnContentFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (_rebuilding) return;
        // 删除目录也触发 Deleted 事件：目录无后缀/命中排除目录时跳过，避免无效 RemoveFile
        if (!IsIndexablePath(e.FullPath)) return;
        Log($"[ContentWatcher] 删除: {Path.GetFileName(e.FullPath)}");
        try
        {
            _contentIndexer.RemoveFile(e.FullPath);
            Log($"删除索引完成: {Path.GetFileName(e.FullPath)}, 内容={_contentIndexer.DocCount}");
            IndexUpdated?.Invoke(); // 通知 UI 刷新当前搜索结果
        }
        catch (Exception ex) { Log($"删除索引失败: {ex.Message}"); }
    }

    private async void OnContentFileRenamed(object sender, RenamedEventArgs e)
    {
        if (_rebuilding) return;
        try
        {
            // 旧路径：曾可索引才删除（精确 term 删除，无匹配则无害）
            if (IsIndexablePath(e.OldFullPath))
                _contentIndexer.RemoveFile(e.OldFullPath);
            // 新路径：通过统一过滤才走完整增量流程，避免 fire-and-forget 导致新文档不可见
            if (IsIndexablePath(e.FullPath))
            {
                await _contentIndexer.IndexFileAsync(e.FullPath);
                _contentIndexer.CommitChanges();
            }
            IndexUpdated?.Invoke(); // 通知 UI 刷新当前搜索结果
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
                ct.ThrowIfCancellationRequested(); // 文件名搜索后检查取消

                var contentResults = _contentSearcher?.Search(query, maxResults: 20) ?? [];
                ct.ThrowIfCancellationRequested(); // 内容搜索后检查取消

                // 按配置的后缀过滤内容搜索结果
                var validExts = new HashSet<string>(_contentExts, StringComparer.OrdinalIgnoreCase);
                contentResults = contentResults
                    .Where(r => validExts.Contains(
                        Path.GetExtension(r.FullPath).TrimStart('.')))
                    .ToList();

                // HashSet 去重 + 过滤磁盘上已不存在的文件（索引脏数据兜底，结果 ≤50 开销可忽略）
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var merged = new List<SearchResult>();

                foreach (var r in fileNameResults)
                {
                    if (seen.Add(r.FullPath) && File.Exists(r.FullPath))
                        merged.Add(r);
                }
                foreach (var r in contentResults)
                {
                    if (seen.Add(r.FullPath) && File.Exists(r.FullPath))
                        merged.Add(r);
                }

                ct.ThrowIfCancellationRequested(); // 去重后检查取消（防止发布过期结果）
                return (IReadOnlyList<SearchResult>)merged;
            }, ct);

            // 兜底：Task.Run 完成后再次检查（防止返回值和事件发布之间的竞态）
            ct.ThrowIfCancellationRequested();
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
                if (PathFilter.IsExcluded(subDir, _excludePatterns)) continue; // 与 watcher 事件过滤共用同一规则
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
