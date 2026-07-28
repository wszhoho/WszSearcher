using System.Windows;
using WszSearcher.ViewModels;

namespace WszSearcher.Views;

public partial class PreviewWindow : Window
{
    private Window? _ownerWindow;
    private bool _isSnapped = true;
    private bool _suppress; // 抑制 LocationChanged 循环
    private const int SnapGap = 0;

    public PreviewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Visibility = Visibility.Collapsed;
        LocationChanged += OnLocationChanged;

        // 拦截关闭事件：隐藏而非销毁，以便下次重新 Show()
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            if (DataContext is MainViewModel vm)
                vm.IsPreviewVisible = false;
        };
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
                    MoveToOwner();
            };
            _ownerWindow.SizeChanged += (_, _) =>
            {
                if (_isSnapped && Visibility == Visibility.Visible)
                    MoveToOwner();
            };
        }
    }

    public void SnapToOwner()
    {
        if (_ownerWindow is null) return;
        _isSnapped = true;
        MoveToOwner();
        if (Visibility != Visibility.Visible) Show();
    }

    private void MoveToOwner()
    {
        if (_ownerWindow is null) return;
        _suppress = true;
        Left = Math.Round(_ownerWindow.Left + _ownerWindow.ActualWidth + SnapGap);
        Top = Math.Round(_ownerWindow.Top);
        Height = Math.Round(_ownerWindow.ActualHeight);
        _suppress = false;
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_suppress || _ownerWindow is null) return; // 程序移动 → 跳过

        var expectedLeft = Math.Round(_ownerWindow.Left + _ownerWindow.ActualWidth + SnapGap);
        var expectedTop = Math.Round(_ownerWindow.Top);
        var distX = Math.Abs(Left - expectedLeft);
        var distY = Math.Abs(Top - expectedTop);

        if (!_isSnapped && distX <= 15 && distY <= 15)
        {
            // 用户拖回主窗口附近 → 重新吸附
            _isSnapped = true;
            MoveToOwner();
        }
        else if (_isSnapped && (distX > 2 || distY > 2))
        {
            // 用户拖离 → 脱离吸附
            _isSnapped = false;
        }
    }
}
