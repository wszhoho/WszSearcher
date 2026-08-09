using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WszSearcher.Core.Localization;
using WszSearcher.Core.Native;
using WszSearcher.Core.Preview;
using WszSearcher.Core.Search;
using WszSearcher.Services;

namespace WszSearcher.ViewModels;

/// <summary>主窗口 ViewModel</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISearchService _searchService;
    private readonly IPreviewService _previewService;
    private CancellationTokenSource? _searchCts;
    private readonly object _searchLock = new(); // 保护 _searchCts 竞态
    private System.Timers.Timer? _debounceTimer; // 搜索防抖
    private System.Timers.Timer? _indexRefreshTimer; // 索引实时更新后自动刷新结果防抖
    private System.Timers.Timer? _postSearchRecycleTimer; // 搜索完成后延迟内存回收
    private System.Timers.Timer? _idleRecycleTimer;       // 周期空闲检测回收
    private DateTime _lastUserInputTime = DateTime.UtcNow; // 最近一次搜索输入时间
    private int _recycling;                               // 0=空闲 1=回收中（防重入）
    private StatusMessage? _lastStatusMessage;            // 最近一条状态消息（语言切换后重译）

    public MainViewModel(ISearchService searchService, IPreviewService previewService)
    {
        _searchService = searchService;
        _previewService = previewService;
        _searchService.ResultsUpdated += OnSearchResultsUpdated;
        _searchService.StatusChanged += OnSearchStatusChanged;
        _searchService.StatusMessage += msg =>
        {
            _lastStatusMessage = msg;
            StatusMessage = LanguageManager.Get(msg.Key, msg.Args);
        };
        _searchService.ProgressChanged += count => IndexProgress = count;
        _searchService.IndexUpdated += OnIndexUpdated;
        LanguageManager.LanguageChanged += OnLanguageChanged; // 运行时切换语言时刷新代码侧文本

        // 初始化防抖定时器（150ms）
        _debounceTimer = new System.Timers.Timer(150) { AutoReset = false };
        _debounceTimer.Elapsed += OnDebounceElapsed;

        // 索引实时更新后自动刷新当前搜索结果（800ms 合并批量文件变更）
        _indexRefreshTimer = new System.Timers.Timer(800) { AutoReset = false };
        _indexRefreshTimer.Elapsed += OnIndexRefreshElapsed;

        // 搜索完成后延迟 10 秒回收内存（给 UI 渲染留出时间）
        _postSearchRecycleTimer = new System.Timers.Timer(10_000) { AutoReset = false };
        _postSearchRecycleTimer.Elapsed += (_, _) => RecycleMemory();

        // 周期性空闲回收（每 60 秒检测一次，超过 120 秒无输入则回收）
        _idleRecycleTimer = new System.Timers.Timer(60_000) { AutoReset = true };
        _idleRecycleTimer.Elapsed += OnIdleRecycleCheck;
        _idleRecycleTimer.Start();

        // 初始化占位提示（构造时状态为 Idle，StatusChanged 不会触发，需显式设置）
        PlaceholderText = LanguageManager.Get("Lang.Main.PlaceholderDefault");
    }

    // ─── 可观察属性 ───

    /// <summary>窗口是否展开（初始折叠，搜索后展开显示结果区域）</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];

    [ObservableProperty]
    private SearchResultViewModel? _selectedResult;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private PreviewResult? _previewContent;

    [ObservableProperty]
    private bool _isPreviewVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private int _indexProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private int _resultCount;

    /// <summary>是否有搜索结果（绑定 UI 显示用）</summary>
    public bool HasResults => HasSearched && ResultCount > 0 && !IsSearching;

    /// <summary>是否搜索无结果（绑定 UI 显示用）</summary>
    public bool HasNoResults => HasSearched && ResultCount == 0 && !IsSearching;

    /// <summary>是否已执行过搜索（区分初始状态和搜索无结果状态）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool _hasSearched;

    [ObservableProperty]
    private string _placeholderText = "";

    /// <summary>搜索输入变更时触发防抖</summary>
    partial void OnSearchTextChanged(string value)
    {
        // 记录用户活动时间，并取消待触发的延迟回收（避免与搜索/渲染冲突）
        _lastUserInputTime = DateTime.UtcNow;
        _postSearchRecycleTimer?.Stop();
        if (_debounceTimer is null) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    /// <summary>防抖定时器到期，执行搜索</summary>
    private async void OnDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            // 原子地取消旧搜索并创建新令牌
            var newCts = CreateSearchToken();

            // 在 UI 线程读取搜索文本（避免跨线程访问属性）
            var query = SearchText;
            if (string.IsNullOrWhiteSpace(query))
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    // 第1步：优先折叠窗口（快速视觉反馈，不阻塞后续操作）
                    _ = dispatcher.InvokeAsync(() =>
                    {
                        IsExpanded = false;
                        IsPreviewVisible = false;
                        HasSearched = false;
                    }, System.Windows.Threading.DispatcherPriority.Normal);

                    // 第2步：延迟清空结果集合（ListBox 容器销毁可与折叠并行）
                    _ = dispatcher.InvokeAsync(() =>
                    {
                        Results.Clear();
                        ResultCount = 0;
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                return;
            }

            await ExecuteSearchAsync(newCts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"防抖定时器异常: {ex.Message}");
        }
    }

    /// <summary>索引实时更新完成：若有激活搜索词，排队自动刷新当前搜索结果</summary>
    private void OnIndexUpdated()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        _indexRefreshTimer?.Stop();
        _indexRefreshTimer?.Start();
    }

    /// <summary>索引刷新防抖到期，重新执行当前搜索（不折叠窗口、不清空结果）</summary>
    private async void OnIndexRefreshElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            await ExecuteSearchAsync(CreateSearchToken());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"索引刷新搜索异常: {ex.Message}");
        }
    }

    /// <summary>原子地取消旧搜索并创建新令牌（锁外取消，防止回调中获取锁导致死锁）</summary>
    private CancellationTokenSource CreateSearchToken()
    {
        CancellationTokenSource? oldCts;
        CancellationTokenSource newCts;
        lock (_searchLock)
        {
            oldCts = _searchCts;
            _searchCts = new CancellationTokenSource();
            newCts = _searchCts;
        }
        // 在锁外 Cancel/Dispose，防止回调中获取锁导致死锁
        oldCts?.Cancel();
        oldCts?.Dispose();
        return newCts;
    }

    /// <summary>执行搜索（输入防抖与索引自动刷新共用）</summary>
    private async Task ExecuteSearchAsync(CancellationTokenSource cts)
    {
        var query = SearchText;
        if (string.IsNullOrWhiteSpace(query)) return;

        try
        {
            await _searchService.SearchAsync(query, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"搜索异常: {ex.Message}");
        }
    }

    partial void OnSelectedResultChanged(SearchResultViewModel? value)
    {
        if (value is null)
        {
            IsPreviewVisible = false;
            PreviewContent = null;
            return;
        }
        // 先关闭上一次预览（确保重新选中时 IsPreviewVisible 变化能触发事件）
        IsPreviewVisible = false;
        // 延迟加载预览，避免同步操作阻塞
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(() => _ = LoadPreviewAsync(value.FullPath)));

        // 焦点还给搜索框（否则 ListBox 选中会抢走焦点，用户感觉"卡死"）
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                var w = System.Windows.Application.Current.MainWindow as MainWindow;
                w?.FocusSearchBox();
            }));
    }

    private async Task LoadPreviewAsync(string filePath)
    {
        try
        {
            // 在后台线程执行文件读取和高亮分段预处理，避免 IO 和字符串搜索阻塞 UI 线程
            var keyword = SearchText;
            var result = await Task.Run(() => _previewService.GetPreviewAsync(filePath, keyword));
            // Core 层占位消息（文件不存在/过大/取消等）携带 StatusKey，由 UI 层翻译为显示文本
            if (result.StatusKey is not null)
                result.Content = LanguageManager.Get(result.StatusKey, result.StatusArgs);
            PreviewContent = result;
            IsPreviewVisible = true;
        }
        catch (Exception ex)
        {
            PreviewContent = new PreviewResult
            {
                Content = LanguageManager.Get("Lang.Main.PreviewLoadFailed", ex switch
                {
                    IOException => LanguageManager.Get("Lang.Main.PreviewBusy"),
                    UnauthorizedAccessException => LanguageManager.Get("Lang.Main.PreviewNoPermission"),
                    _ => ex.Message
                }),
                Type = PreviewType.Text,
                Title = Path.GetFileName(filePath),
                FilePath = filePath
            };
            IsPreviewVisible = true;
        }
    }

    private void OnSearchResultsUpdated(IReadOnlyList<Core.Models.SearchResult> results)
    {
        // 丢弃过期结果：搜索框已被清空，当前不应展示任何旧数据
        if (string.IsNullOrWhiteSpace(SearchText))
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            Results.Clear();
            foreach (var r in results)
            {
                Results.Add(new SearchResultViewModel(r));
            }
            ResultCount = Results.Count;
            HasSearched = true;
            IsExpanded = true; // 搜索完成后展开窗口显示结果
        });

        // 搜索完成：延迟 10 秒执行内存回收（LOH 压缩 + 工作集释放）
        _postSearchRecycleTimer?.Stop();
        _postSearchRecycleTimer?.Start();
    }

    /// <summary>周期检测空闲状态，长时间无输入则回收内存</summary>
    private void OnIdleRecycleCheck(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // 超过 120 秒无搜索输入才回收，避免与用户交互冲突
        if ((DateTime.UtcNow - _lastUserInputTime).TotalSeconds > 120)
            RecycleMemory();
    }

    /// <summary>压缩大对象堆并清空工作集，降低任务管理器内存占用（后台线程执行）</summary>
    private void RecycleMemory()
    {
        if (Interlocked.Exchange(ref _recycling, 1) == 1) return;
        try
        {
            // 下一次阻塞式全量 GC 时压缩大对象堆（LOH），让大数组内存可复用
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            // 把空闲物理页换出到页面文件，任务管理器"内存(活动)"列回落
            MemoryApi.EmptyWorkingSet();
        }
        catch
        {
            // 内存回收属非关键路径，失败忽略
        }
        finally
        {
            Interlocked.Exchange(ref _recycling, 0);
        }
    }

    /// <summary>语言切换后刷新代码侧状态文本（XAML DynamicResource 自动刷新，无需处理）</summary>
    private void OnLanguageChanged()
    {
        if (_lastStatusMessage is not null)
            StatusMessage = LanguageManager.Get(_lastStatusMessage.Key, _lastStatusMessage.Args);
        PlaceholderText = _searchService.Status switch
        {
            SearchStatus.Indexing => LanguageManager.Get("Lang.Main.PlaceholderIndexing"),
            SearchStatus.Ready => LanguageManager.Get("Lang.Main.PlaceholderReady"),
            _ => LanguageManager.Get("Lang.Main.PlaceholderDefault")
        };
    }

    private void OnSearchStatusChanged(SearchStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            IsSearching = status == SearchStatus.Searching;
            IsIndexing = status == SearchStatus.Indexing;

            PlaceholderText = status switch
            {
                SearchStatus.Indexing => LanguageManager.Get("Lang.Main.PlaceholderIndexing"),
                SearchStatus.Ready => LanguageManager.Get("Lang.Main.PlaceholderReady"),
                _ => LanguageManager.Get("Lang.Main.PlaceholderDefault")
            };
        });
    }

    [RelayCommand]
    private void HidePreview()
    {
        SelectedResult = null;
        IsPreviewVisible = false;
        PreviewContent = null;
    }

    /// <summary>直接打开文件</summary>
    [RelayCommand]
    private void OpenFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开文件失败: {ex.Message}");
        }
    }

    /// <summary>复制文件到剪贴板</summary>
    [RelayCommand]
    private void CopyFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
        try
        {
            System.Windows.Clipboard.SetFileDropList(
                new System.Collections.Specialized.StringCollection { filePath });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"复制文件失败: {ex.Message}");
        }
    }

    /// <summary>复制文件名到剪贴板</summary>
    [RelayCommand]
    private void CopyFileName(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            var name = Path.GetFileName(filePath);
            System.Windows.Clipboard.SetText(name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"复制文件名失败: {ex.Message}");
        }
    }

    /// <summary>复制完整路径到剪贴板</summary>
    [RelayCommand]
    private void CopyFullPath(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            System.Windows.Clipboard.SetText(filePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"复制完整路径失败: {ex.Message}");
        }
    }

    /// <summary>在资源管理器中打开文件所在目录并选中文件</summary>
    [RelayCommand]
    private void OpenFileLocation(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"打开文件位置失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Quit()
    {
        _debounceTimer?.Dispose();
        _postSearchRecycleTimer?.Dispose();
        _idleRecycleTimer?.Dispose();

        // 通过 App 类触发正确的退出流程（设置 PrepareExit 标志、释放资源）
        if (System.Windows.Application.Current is App app)
        {
            app.TriggerApplicationExit();
        }
        else
        {
            System.Windows.Application.Current.Shutdown();
        }
    }
}
