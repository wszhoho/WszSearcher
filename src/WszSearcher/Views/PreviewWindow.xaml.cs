using System.Windows;
using WszSearcher.ViewModels;

namespace WszSearcher.Views;

/// <summary>浮动预览窗口——吸附到主窗口右侧，手动拖离后保持脱离</summary>
public partial class PreviewWindow : Window
{
    private Window? _ownerWindow;
    private bool _isSnapped = true;
    private const int Gap = 8;
    private bool _suppressLocationChanged; // 防止自己移动时触发检测循环

    public PreviewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Visibility = Visibility.Collapsed;
        LocationChanged += OnLocationChanged;
    }

    public void SetOwnerWindow(Window owner)
    {
        _ownerWindow = owner;
        Owner = owner;

        if (_ownerWindow is not null)
        {
            _ownerWindow.LocationChanged += (_, _) =>
            {
                if (_isSnapped && Visibility == Visibility.Visible)
                {
                    _suppressLocationChanged = true;
                    Left = _ownerWindow.Left + _ownerWindow.ActualWidth + Gap;
                    Top = _ownerWindow.Top;
                    _suppressLocationChanged = false;
                }
            };
            _ownerWindow.SizeChanged += (_, _) =>
            {
                if (_isSnapped && Visibility == Visibility.Visible)
                {
                    _suppressLocationChanged = true;
                    Left = _ownerWindow.Left + _ownerWindow.ActualWidth + Gap;
                    Top = _ownerWindow.Top;
                    Height = _ownerWindow.ActualHeight;
                    _suppressLocationChanged = false;
                }
            };
        }
    }

    public void SnapToOwner()
    {
        if (_ownerWindow is null) return;
        _isSnapped = true;
        _suppressLocationChanged = true;
        Left = _ownerWindow.Left + _ownerWindow.ActualWidth + Gap;
        Top = _ownerWindow.Top;
        Height = _ownerWindow.ActualHeight;
        _suppressLocationChanged = false;

        if (Visibility != Visibility.Visible)
            Show();
        Activate();
    }

    /// <summary>用户手动拖动窗口 → 脱离吸附</summary>
    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_suppressLocationChanged || !_isSnapped) return;
        // 程序自己移动窗口时 suppress 为 true，不会执行到这里。
        // 只有用户手动拖动才会触发 → 脱离吸附。
        _isSnapped = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _ownerWindow = null;
        base.OnClosed(e);
    }
}
