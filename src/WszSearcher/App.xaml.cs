using System.Windows;
using WszSearcher.Core.Preview;
using WszSearcher.Core.Search;
using WszSearcher.Services;
using WszSearcher.ViewModels;
using WszSearcher.Views;

namespace WszSearcher;

    public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private PreviewWindow? _previewWindow; // 浮动预览窗口
    private AppSettings? _settings;
    private ISearchService? _searchService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 加载设置
        _settings = AppSettings.Load();

        // 同步开机自启注册表
        SyncAutoStart();

        // 注入
        var driveLetter = _settings.IndexPaths.Count > 0 && _settings.IndexPaths[0].Length > 0
            ? _settings.IndexPaths[0][0] : 'C';
        var searchService = new SearchService(driveLetter);
        _searchService = searchService;
        var previewService = new PreviewService();
        _mainViewModel = new MainViewModel(searchService, previewService);

        _mainWindow = new MainWindow(_mainViewModel, _settings);
        _mainWindow.Show();
        _mainWindow.Hide(); // 开机启动时隐藏到托盘

        // 创建浮动预览窗口（附属于主窗口，初始隐藏）
        _previewWindow = new PreviewWindow(_mainViewModel);
        _previewWindow.SetOwnerWindow(_mainWindow);

        // 监听 IsPreviewVisible 变化以自动显示/隐藏预览窗口
        _mainViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.IsPreviewVisible)) return;
            if (_mainViewModel.IsPreviewVisible)
                _previewWindow?.SnapToOwner();
            else
                _previewWindow?.Hide();
        };

        // 强制创建托盘图标（WPF 中带 Key 的资源是惰性实例化，必须主动访问才能创建）
        _ = FindResource("TrayIcon");

        searchService.SetContentExtensions(_settings.ContentIndexExtensions);

        // 有索引路径时才自动初始化
        if (_settings.IndexPaths.Count > 0)
        {
            searchService.SetIndexPaths(_settings.IndexPaths);
            _ = searchService.InitializeAsync();
        }
    }

    private void SyncAutoStart()
    {
        try
        {
            var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (rk is null || _settings is null) return;

            if (_settings.AutoStart)
                rk.SetValue("WszSearcher", $"\"{Environment.ProcessPath}\"");
            else
                rk.DeleteValue("WszSearcher", false);
            rk.Close();
        }
        catch { }
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        if (_mainWindow?.Visibility == Visibility.Visible) _mainWindow.Hide();
        else ShowMainWindow();
    }

    private void OnShowWindow(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null || !_settingsWindow.IsVisible)
        {
            _settingsWindow = new SettingsWindow(new SettingsViewModel(_settings!, _searchService!));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Owner = _mainWindow;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnShowAbout(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is null || !_aboutWindow.IsVisible)
        {
            _aboutWindow = new AboutWindow { Owner = _mainWindow };
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.ShowDialog();
    }

    private void OnExitApp(object sender, RoutedEventArgs e)
    {
        TriggerApplicationExit();
    }

    /// <summary>执行完整的应用退出流程（保存设置、清理资源）</summary>
    internal void TriggerApplicationExit()
    {
        SaveSettings();

        // 关闭预览窗口
        _previewWindow?.Close();
        _previewWindow = null;

        // 关闭设置窗口（触发 SettingsViewModel.Dispose 取消事件订阅）
        _settingsWindow?.Close();
        _settingsWindow = null;

        // 通知主窗口真正退出（触发 OnClosed 清理 HotkeyService）
        _mainWindow?.PrepareExit();
        _mainWindow?.Close();

        Current.Shutdown();
    }

    private void OnAppExit(object sender, ExitEventArgs e) => SaveSettings();

    private void ShowMainWindow()
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
        _mainWindow?.FocusSearchBox();
    }

    internal void SaveSettings()
    {
        if (_mainWindow is not null && _settings is not null)
        {
            _settings.WindowWidth = _mainWindow.Width;
            _settings.WindowHeight = _mainWindow.Height;
            _settings.WindowLeft = _mainWindow.Left;
            _settings.WindowTop = _mainWindow.Top;
        }
        _settings?.Save();
    }

    /// <summary>应用新的全局快捷键配置（设置保存后调用）</summary>
    internal void ApplyHotkey(uint modifiers, uint key)
    {
        if (_mainWindow is null) return;

        var success = _mainWindow.ReregisterHotkey(modifiers, key);
        if (!success)
        {
            System.Windows.MessageBox.Show(
                "快捷键注册失败，可能被其他程序占用，请重新设置。",
                "快捷键冲突", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
