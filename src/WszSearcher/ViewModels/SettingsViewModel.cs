using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WszSearcher.Core.Search;
using WszSearcher.Services;

namespace WszSearcher.ViewModels;

/// <summary>设置窗口 ViewModel</summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly ISearchService _searchService;
    private bool _disposed;

    public SettingsViewModel(AppSettings settings, ISearchService searchService)
    {
        _settings = settings;
        _searchService = searchService;

        // 加载现有设置
        _autoStart = settings.AutoStart;
        _maxResults = settings.MaxResults;

        // 加载快捷键设置
        var mods = settings.HotkeyModifiers;
        _hotkeyAlt = (mods & GlobalHotkeyService.MOD_ALT) != 0;
        _hotkeyCtrl = (mods & GlobalHotkeyService.MOD_CONTROL) != 0;
        _hotkeyShift = (mods & GlobalHotkeyService.MOD_SHIFT) != 0;
        _hotkeyWin = (mods & GlobalHotkeyService.MOD_WIN) != 0;
        _selectedKey = AvailableKeys.FirstOrDefault(k => k.Code == settings.HotkeyKey)
                        ?? AvailableKeys.First();

        foreach (var path in settings.IndexPaths)
            IndexPaths.Add(new ObservablePath { Path = path });

        // 订阅索引状态事件
        _searchService.StatusChanged += OnSearchStatusChanged;
        _searchService.StatusMessage += OnStatusMessage;
        _searchService.ProgressChanged += OnProgressChanged;

        // 同步当前状态（可能在窗口打开前已变更）
        OnSearchStatusChanged(_searchService.Status);
        UpdateIndexCounts();
    }

    // ─── 索引路径 ───

    public ObservableCollection<ObservablePath> IndexPaths { get; } = [];

    [RelayCommand]
    private void AddIndexPath()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择要索引的文件夹" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var existing = IndexPaths.FirstOrDefault(p =>
                string.Equals(p.Path, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                IndexPaths.Add(new ObservablePath { Path = dialog.SelectedPath });
            else
                System.Windows.MessageBox.Show("该路径已存在", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void RemoveIndexPath(ObservablePath? path)
    {
        if (path is not null)
            IndexPaths.Remove(path);
    }

    // ─── 排除模式 ───

    public string ExcludePatternsText
    {
        get => _settings.ExcludePaths is not null
            ? string.Join("; ", _settings.ExcludePaths.Select(p => p.Trim('*', '\\')))
            : "";
        set
        {
            _settings.ExcludePaths = (value ?? "")
                .Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => $"*\\{p.Trim().Trim('*', '\\')}")
                .Where(p => p.Length > 2)
                .ToList();
            OnPropertyChanged();
        }
    }

    // ─── 开机自启 ───

    [ObservableProperty]
    private bool _autoStart;

    partial void OnAutoStartChanged(bool value)
    {
        _settings.AutoStart = value;
    }

    // ─── 最大结果数 ───

    [ObservableProperty]
    private int _maxResults = 50;

    partial void OnMaxResultsChanged(int value)
    {
        _settings.MaxResults = Math.Clamp(value, 10, 500);
    }

    // ─── 全文索引文件后缀 ───

    public string ExtensionsText
    {
        get => string.Join(", ", _settings.ContentIndexExtensions ?? []);
        set
        {
            _settings.ContentIndexExtensions = (value ?? "")
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
                .Where(e => e.Length > 0)
                .Distinct()
                .ToList();
            OnPropertyChanged();
        }
    }

    // ─── 索引管理（新增） ───

    [ObservableProperty]
    private string _indexStatusMessage = "就绪";

    [ObservableProperty]
    private int _indexProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotIndexing))]
    private bool _isIndexing;

    /// <summary>是否未在索引中（用于按钮可见性绑定）</summary>
    public bool IsNotIndexing => !IsIndexing;

    [ObservableProperty]
    private string _fileNameCount = "—";

    [ObservableProperty]
    private string _contentCount = "—";

    // ─── 快捷键配置 ───

    // 修饰键
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyPreview))]
    private bool _hotkeyCtrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyPreview))]
    private bool _hotkeyAlt = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyPreview))]
    private bool _hotkeyShift;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyPreview))]
    private bool _hotkeyWin;

    // 可选按键列表
    public List<HotkeyItem> AvailableKeys { get; } =
    [
        new(0x20, "Space"),
        new(0x09, "Tab"),
        new(0x0D, "Enter"),
        new(0x1B, "Esc"),
        new(0x2E, "Delete"),
        new(0x21, "PageUp"),
        new(0x22, "PageDown"),
        new(0x23, "End"),
        new(0x24, "Home"),
        new(0x25, "←"),
        new(0x26, "↑"),
        new(0x27, "→"),
        new(0x28, "↓"),
        .. Enumerable.Range(0, 26).Select(i => new HotkeyItem((uint)(0x41 + i), $"{(char)('A' + i)}")),
        .. Enumerable.Range(0, 10).Select(i => new HotkeyItem((uint)(0x30 + i), $"{i}")),
        .. Enumerable.Range(1, 12).Select(i => new HotkeyItem((uint)(0x6F + i), $"F{i}")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyPreview))]
    private HotkeyItem? _selectedKey;

    /// <summary>快捷键预览文本</summary>
    public string HotkeyPreview
    {
        get
        {
            var parts = new List<string>();
            if (HotkeyCtrl) parts.Add("Ctrl");
            if (HotkeyAlt) parts.Add("Alt");
            if (HotkeyShift) parts.Add("Shift");
            if (HotkeyWin) parts.Add("Win");
            var keyText = SelectedKey?.Display ?? "...";
            return parts.Count > 0 ? $"{string.Join(" + ", parts)} + {keyText}" : keyText;
        }
    }

    /// <summary>计算当前配置的 Win32 修饰键值</summary>
    private uint BuildModifiers()
    {
        uint m = 0;
        if (HotkeyAlt) m |= GlobalHotkeyService.MOD_ALT;
        if (HotkeyCtrl) m |= GlobalHotkeyService.MOD_CONTROL;
        if (HotkeyShift) m |= GlobalHotkeyService.MOD_SHIFT;
        if (HotkeyWin) m |= GlobalHotkeyService.MOD_WIN;
        if (m == 0) m = GlobalHotkeyService.MOD_ALT; // 至少一个修饰键
        return m;
    }

    /// <summary>测试快捷键冲突</summary>
    [RelayCommand]
    private void TestHotkeyConflict()
    {
        var modifiers = BuildModifiers();
        if (SelectedKey is null)
        {
            System.Windows.MessageBox.Show("请先选择按键", "提示");
            return;
        }

        // 通过主窗口获取 HWND 进行冲突检测
        var mainWindow = System.Windows.Application.Current.MainWindow;
        if (mainWindow is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
        var hasConflict = GlobalHotkeyService.CheckConflict(hwnd, modifiers, SelectedKey.Code,
            _settings.HotkeyModifiers, _settings.HotkeyKey);

        if (hasConflict)
            System.Windows.MessageBox.Show($"快捷键 {HotkeyPreview} 已被其他程序占用，请更换", "快捷键冲突",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        else
            System.Windows.MessageBox.Show($"快捷键 {HotkeyPreview} 可用", "检测通过",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>快捷键配置项</summary>
    public record HotkeyItem(uint Code, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>重建索引命令</summary>
    [RelayCommand]
    private async Task RebuildIndex()
    {
        if (IsIndexing) return;

        // 先同步设置中的索引路径到 SearchService
        _settings.IndexPaths = IndexPaths.Select(p => p.Path).ToList();
        _settings.AutoStart = AutoStart;
        _settings.MaxResults = MaxResults;
        _settings.Save();

        // 设置索引路径列表
        var paths = _settings.IndexPaths.ToList();
        if (paths.Count == 0)
        {
            System.Windows.MessageBox.Show("请先添加索引路径", "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _searchService.SetIndexPaths(paths);

        // 设置全文索引后缀
        _searchService.SetContentExtensions(_settings.ContentIndexExtensions);

        // 立即清零显示
        FileNameCount = "0";
        ContentCount = "0";

        try
        {
            await _searchService.RebuildIndexAsync();
        }
        catch (Exception ex)
        {
            IndexStatusMessage = $"重建失败：{ex.Message}";
        }
    }

    /// <summary>停止索引命令</summary>
    [RelayCommand]
    private void StopIndex()
    {
        _searchService.CancelIndex();
    }

    /// <summary>刷新索引统计</summary>
    [RelayCommand]
    private void RefreshIndexStats()
    {
        UpdateIndexCounts();
    }

    private void UpdateIndexCounts()
    {
        FileNameCount = _searchService.FileNameIndexCount.ToString("N0");
        ContentCount = _searchService.ContentIndexCount.ToString("N0");
    }

    private void OnSearchStatusChanged(SearchStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            IsIndexing = status == SearchStatus.Indexing;
            UpdateIndexCounts();
        });
    }

    private void OnStatusMessage(string msg)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.InvokeAsync(() => IndexStatusMessage = msg);
    }

    private void OnProgressChanged(int count)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.InvokeAsync(() =>
        {
            IndexProgress = count;
            UpdateIndexCounts();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchService.StatusChanged -= OnSearchStatusChanged;
        _searchService.StatusMessage -= OnStatusMessage;
        _searchService.ProgressChanged -= OnProgressChanged;
    }

    // ─── 命令 ───

    [RelayCommand]
    private void Save()
    {
        _settings.IndexPaths = IndexPaths.Select(p => p.Path).ToList();
        _settings.AutoStart = AutoStart;
        _settings.MaxResults = MaxResults;

        // 保存快捷键配置
        _settings.HotkeyModifiers = BuildModifiers();
        _settings.HotkeyKey = SelectedKey?.Code ?? 0x20;
        _settings.Save();

        // 应用快捷键：通知 App 重新注册
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);

        // 开机自启
        SetAutoStart(_settings.AutoStart);

        // 通知用户
        System.Windows.MessageBox.Show("设置已保存。",
            "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void SetAutoStart(bool enable)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            if (enable)
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Create /F /SC ONLOGON /TN \"WszSearcher\" /TR \"\\\"{exe}\\\"\" /RL HIGHEST",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/Delete /F /TN \"WszSearcher\"",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
        }
        catch { /* 权限不足时跳过 */ }
    }

    [RelayCommand]
    private void Cancel()
    {
        // 关闭窗口（由 code-behind 处理）
    }

    [RelayCommand]
    private void ShowAbout()
    {
        new Views.AboutWindow { Owner = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault() }
            .ShowDialog();
    }
}

/// <summary>列表用可观察路径包装类</summary>
public partial class ObservablePath : ObservableObject
{
    [ObservableProperty]
    private string _path = "";
}
