using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WszSearcher.Services;
using WszSearcher.ViewModels;

namespace WszSearcher;

public partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private bool _isExiting;

    // WM_NCHITTEST 返回值（用来自定义无边框窗口的拖动区和调整大小区）
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeMargin = 6; // 边缘调整大小灵敏度
    private const double CollapsedHeight = 52;  // 搜索框 48px + Grid margin 上下各 2px
    private const double RowHeightPerItem = 58;   // 每条结果项约 58px
    private const double ResultHeaderHeight = 34; // 结果数量提示栏
    private const int MaxVisibleRows = 6;         // 最多显示 6 行
    private const double MaxResultHeight = RowHeightPerItem * MaxVisibleRows + ResultHeaderHeight;

    private double _expandedHeight;
    private bool _isExpanded; // 本地缓存避免重复计算

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        // 恢复窗口位置和大小
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
        {
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
        }
        else
        {
            // 默认位置：水平居中，垂直距顶部 1/3
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = SystemParameters.PrimaryScreenHeight / 3;
        }
        // 若设置中还是旧默认值（用户从未调整过），使用新的默认值
        if (_settings.WindowWidth == 760) _settings.WindowWidth = 560;
        Width = Math.Clamp(_settings.WindowWidth, 400, 1200);
        Height = CollapsedHeight; // 初始折叠，仅搜索栏

        // 监听 ViewModel 的 IsExpanded 变化以动态调整窗口高度
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 注册全局热键（使用设置中的快捷键配置）
        _hotkeyService = new GlobalHotkeyService(this,
            _settings.HotkeyModifiers, _settings.HotkeyKey);
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        _hotkeyService.Register();

        // 窗口关闭时最小化到托盘而非退出
        Closing += OnClosing;

        // 搜索框自动获取焦点
        Loaded += OnLoaded;

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>监听 IsExpanded/ResultCount 变化，动态调整窗口</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsExpanded))
        {
            if (_viewModel.IsExpanded && !_isExpanded)
            {
                _isExpanded = true;
                ResultRow.Height = new GridLength(1, GridUnitType.Star);
                SearchBarBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);
                SearchBarBorder.BorderThickness = new Thickness(0, 0, 0, 1); // 底部边框分割线
                AdjustExpandedHeight();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.ResultCount))
        {
            if (_isExpanded) AdjustExpandedHeight();
        }
    }

    /// <summary>根据结果数量自适应窗口高度</summary>
    private void AdjustExpandedHeight()
    {
        var count = _viewModel.ResultCount;
        var resultHeight = count <= 0 ? MaxResultHeight
            : Math.Min(RowHeightPerItem * count + ResultHeaderHeight, MaxResultHeight);

        _expandedHeight = CollapsedHeight + resultHeight + 16; // 16px 窗口边距
        Height = _expandedHeight;
    }

    /// <summary>窗口初始化后添加钩子，并设置圆角裁剪</summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProcHitTest);

        // 圆角裁剪：用 RectangleGeometry 裁剪窗口
        UpdateWindowClip();
        SizeChanged += (_, _) => UpdateWindowClip();
    }

    private void UpdateWindowClip()
    {
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            Clip = new RectangleGeometry(
                new System.Windows.Rect(0, 0, ActualWidth, ActualHeight),
                10, 10); // 10px 圆角
        }
    }

    /// <summary>处理 WM_NCHITTEST：自定义无边框窗口的拖动区和调整大小区</summary>
    private IntPtr WndProcHitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        // 屏幕坐标 → 窗口坐标
        // 屏幕坐标 → 窗口坐标
        var screenPoint = new System.Windows.Point(
            (int)(lParam.ToInt64() & 0xFFFF),
            (int)(lParam.ToInt64() >> 16));
        var clientPoint = PointFromScreen(screenPoint);

        var w = ActualWidth;
        var h = ActualHeight;

        // 边缘调整大小区域
        var onLeft = clientPoint.X <= ResizeMargin;
        var onRight = clientPoint.X >= w - ResizeMargin;
        var onTop = clientPoint.Y <= ResizeMargin;
        var onBottom = clientPoint.Y >= h - ResizeMargin;

        if (onTop && onLeft)     { handled = true; return (IntPtr)HTTOPLEFT; }
        if (onTop && onRight)    { handled = true; return (IntPtr)HTTOPRIGHT; }
        if (onBottom && onLeft)  { handled = true; return (IntPtr)HTBOTTOMLEFT; }
        if (onBottom && onRight) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
        if (onLeft)              { handled = true; return (IntPtr)HTLEFT; }
        if (onRight)             { handled = true; return (IntPtr)HTRIGHT; }
        if (onTop)               { handled = true; return (IntPtr)HTTOP; }
        if (onBottom)            { handled = true; return (IntPtr)HTBOTTOM; }

        // 搜索栏区域作为拖动区（但排除关闭按钮）
        // 关闭按钮位置：右侧 32px 宽，搜索栏顶部
        var closeBtnLeft = w - 2 - 48; // 外 Margin(2) + 按钮宽(48)
        var inCloseButton = clientPoint.X >= closeBtnLeft && clientPoint.Y <= CollapsedHeight;

        if (clientPoint.Y <= CollapsedHeight && !inCloseButton)
        {
            handled = true;
            return (IntPtr)HTCAPTION;
        }

        return IntPtr.Zero;
    }

    /// <summary>让搜索框获取焦点（被系统托盘恢复时调用）</summary>
    public void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusSearchBox();
    }

    /// <summary>搜索栏拖动窗口</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
            DragMove();
    }

    /// <summary>窗口关闭时最小化到托盘（不退出）；真正退出时不拦截</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 保存窗口位置
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();

        if (_isExiting)
        {
            // 真正退出时允许窗口关闭，触发 OnClosed 清理资源
            e.Cancel = false;
            return;
        }

        // 最小化到托盘而不是关闭
        Hide();
        e.Cancel = true;
    }

    /// <summary>由 App.OnExitApp 调用，设置真正退出标志</summary>
    public void PrepareExit()
    {
        _isExiting = true;
    }

    /// <summary>重新注册全局快捷键（设置变更后调用），返回是否成功</summary>
    public bool ReregisterHotkey(uint modifiers, uint key)
    {
        return _hotkeyService.Reregister(modifiers, key);
    }

    /// <summary>全局热键触发：切换窗口显示状态</summary>
    private void OnGlobalHotkeyPressed()
    {
        Dispatcher.Invoke(() =>
        {
            if (Visibility == Visibility.Visible)
            {
                HideWindow();
            }
            else
            {
                ShowWindow();
            }
        });
    }

    /// <summary>右键点击时先选中该项，确保 ContextMenu 操作正确的文件</summary>
    private void OnResultListRightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not System.Windows.Controls.ListBoxItem)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is System.Windows.Controls.ListBoxItem item)
            item.IsSelected = true;
    }

    /// <summary>显示窗口并定位到屏幕顶部中央</summary>
    private void ShowWindow()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Width = Math.Min(_settings.WindowWidth, screenWidth * 0.6);
        Left = (screenWidth - Width) / 2;
        Top = 60;

        Show();
        Activate();
        FocusSearchBox();
    }

    /// <summary>隐藏窗口（最小化到托盘）</summary>
    private void HideWindow()
    {
        Hide();
    }

    /// <summary>关闭按钮：隐藏到托盘</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    /// <summary>点击空白区域隐藏预览</summary>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        var pos = e.GetPosition(this);
        var hit = VisualTreeHelper.HitTest(this, pos);

        if (hit?.VisualHit is not null)
        {
            var element = hit.VisualHit as DependencyObject;
            while (element is not null)
            {
                if (element == ResultList)
                    return;
                element = VisualTreeHelper.GetParent(element);
            }

            if (_viewModel.IsPreviewVisible)
            {
                _viewModel.HidePreviewCommand.Execute(null);
            }
        }
    }

    /// <summary>按 ESC 键隐藏窗口</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideWindow();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    /// <summary>真正关闭时释放资源</summary>
    protected override void OnClosed(EventArgs e)
    {
        _hotkeyService.Dispose();
        base.OnClosed(e);
    }
}
