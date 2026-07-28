using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WszSearcher.ViewModels;

namespace WszSearcher.Views;

public partial class PreviewWindow : Window
{
    private Window? _ownerWindow;
    private bool _isSnapped = true;
    private bool _suppress;
    private const int SnapGap = 0;

    public PreviewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Visibility = Visibility.Collapsed;
        LocationChanged += OnLocationChanged;

        // 监听搜索词和预览内容变化，自动高亮
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SearchText) or nameof(MainViewModel.PreviewContent))
                ApplyHighlight();
        };

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            if (DataContext is MainViewModel vm)
                vm.IsPreviewVisible = false;
        };

        Loaded += (_, _) =>
        {
            if (_isSnapped && _ownerWindow is not null)
                MoveToOwner();
        };
    }

    public void SetOwnerWindow(Window owner)
    {
        _ownerWindow = owner;
        Owner = owner;
        if (_ownerWindow is null) return;

        _ownerWindow.LocationChanged += (_, _) => OnOwnerMoved();
        _ownerWindow.SizeChanged += (_, _) => OnOwnerMoved();
    }

    public void SnapToOwner()
    {
        if (_ownerWindow is null) return;
        _isSnapped = true;
        MoveToOwner();
        if (Visibility != Visibility.Visible) Show();
    }

    private void OnOwnerMoved()
    {
        if (_isSnapped && Visibility == Visibility.Visible)
            FollowOwner();
    }

    private void FollowOwner()
    {
        if (_ownerWindow is null) return;
        _suppress = true;
        Left = Math.Round(_ownerWindow.Left + _ownerWindow.ActualWidth + SnapGap);
        Top = Math.Round(_ownerWindow.Top);
        _suppress = false;
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
        if (_suppress || _ownerWindow is null) return;

        var expectL = Math.Round(_ownerWindow.Left + _ownerWindow.ActualWidth + SnapGap);
        var expectT = Math.Round(_ownerWindow.Top);
        var dx = Math.Abs(Left - expectL);
        var dy = Math.Abs(Top - expectT);
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (_isSnapped && dist > 20)
            _isSnapped = false;
        else if (!_isSnapped && dist <= 38)
        {
            _isSnapped = true;
            MoveToOwner();
        }
    }

    private int _matchIndex = -1;
    private int _matchCount;

    /// <summary>根据搜索词高亮预览文本，并自动滚动到第一个匹配</summary>
    private void ApplyHighlight()
    {
        // DataTrigger 可能在异步更新 Content，延迟重试确保 TextBlock 已渲染
        Dispatcher.BeginInvoke(new Action(ApplyHighlightCore), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyHighlightCore()
    {
        if (DataContext is not MainViewModel vm) return;
        var text = vm.PreviewContent?.Content;
        var keyword = vm.SearchText?.Trim();

        var tb = FindVisualChild<TextBlock>(PreviewContentControl);
        if (tb is null) return;

        tb.Inlines.Clear();
        _matchIndex = -1;
        _matchCount = 0;

        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword) || text.Length < keyword.Length)
        {
            if (!string.IsNullOrEmpty(text))
                tb.Inlines.Add(new Run(text));
            UpdateMatchLabel();
            return;
        }

        var runs = new List<Run>();
        var idx = 0;
        while (idx < text.Length)
        {
            var pos = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                runs.Add(new Run(text[idx..]));
                break;
            }

            if (pos > idx)
                runs.Add(new Run(text[idx..pos]));

            var matchRun = new Run(text[pos..(pos + keyword.Length)])
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE4, 0xB3)),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A))
            };
            runs.Add(matchRun);
            _matchCount++;
            idx = pos + keyword.Length;
        }

        foreach (var r in runs) tb.Inlines.Add(r);
        UpdateMatchLabel();

        // 再延迟滚动到第一个匹配
        Dispatcher.BeginInvoke(new Action(() => ScrollToMatch(-1)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnPrevMatch(object sender, RoutedEventArgs e) => ScrollToMatch(-1);
    private void OnNextMatch(object sender, RoutedEventArgs e) => ScrollToMatch(1);

    private void ScrollToMatch(int direction)
    {
        var tb = FindVisualChild<TextBlock>(PreviewContentControl);
        if (tb is null || _matchCount == 0) return;

        _matchIndex += direction;
        if (_matchIndex >= _matchCount) _matchIndex = 0;
        if (_matchIndex < 0) _matchIndex = _matchCount - 1;

        // 找到第 N 个高亮 Run，滚动到它
        var match = 0;
        foreach (var inline in tb.Inlines)
        {
            if (inline is Run { Background: not null } && match++ == _matchIndex)
            {
                inline.BringIntoView();
                UpdateMatchLabel();
                return;
            }
        }
    }

    private void UpdateMatchLabel()
    {
        if (_matchCount == 0)
            MatchLabel.Text = "";
        else
            MatchLabel.Text = $"{_matchIndex + 1}/{_matchCount}";
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }
}
