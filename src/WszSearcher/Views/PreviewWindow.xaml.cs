using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WszSearcher.Core.Preview;
using WszSearcher.ViewModels;

namespace WszSearcher.Views;

public partial class PreviewWindow : Window
{
    private Window? _ownerWindow;
    private bool _isSnapped = true;
    private bool _suppress;
    private const int SnapGap = 0;
    private int _matchIndex = -1;
    private int _matchCount;

    public PreviewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Visibility = Visibility.Collapsed;
        LocationChanged += OnLocationChanged;

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

    private void ApplyHighlight()
    {
        Dispatcher.BeginInvoke(new Action(ApplyHighlightCore), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyHighlightCore()
    {
        if (DataContext is not MainViewModel vm) return;
        var content = vm.PreviewContent;
        if (content is null) return;
        var text = content.Content ?? "";

        // 图片类型不渲染文本
        if (content.Type == PreviewType.Image)
        {
            var imgPath = content.ImagePath ?? content.FilePath;
            if (!string.IsNullOrEmpty(imgPath))
            {
                PreviewContentHost.Child = new System.Windows.Controls.Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imgPath)),
                    MaxWidth = 360, MaxHeight = 500, Stretch = System.Windows.Media.Stretch.Uniform
                };
            }
            return;
        }

        // 构建 RichTextBox（统一深色背景，所有类型一致）
        var rtb = new System.Windows.Controls.RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4))
        };
        rtb.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Paragraph.MarginProperty, new Thickness(0)) } });

        // 根据类型设置差异样式（背景和前景已统一为深色，仅字体和边框有区别）
        switch (content.Type)
        {
            case PreviewType.Code:
                rtb.FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New");
                rtb.FontSize = 12;
                break;
            case PreviewType.RichText:
                rtb.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3E, 0x3E, 0x3E));
                rtb.BorderThickness = new Thickness(1);
                break;
        }

        PreviewContentHost.Child = rtb;

        // 高亮搜索词——优先使用后台线程预处理的高亮分段，避免 UI 线程字符串搜索
        var para = new Paragraph();
        _matchIndex = -1;
        _matchCount = 0;

        var segments = content.HighlightSegments;
        if (segments is { Count: > 0 })
        {
            // 使用预处理分段构建 Run 元素（后台线程已完成 IndexOf 搜索，UI 线程仅负责创建 UI 元素）
            foreach (var seg in segments)
            {
                var run = new Run(seg.Text);
                if (seg.IsHighlight)
                {
                    run.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE4, 0xB3));
                    run.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
                    _matchCount++;
                }
                para.Inlines.Add(run);
            }
        }
        else
        {
            // 无关键词或分段数据，显示纯文本
            para.Inlines.Add(new Run(text));
        }

        rtb.Document.Blocks.Add(para);
        UpdateMatchLabel();
        Dispatcher.BeginInvoke(new Action(() => ScrollToMatch(-1)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCopyContent(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !string.IsNullOrEmpty(vm.PreviewContent?.Content))
            System.Windows.Clipboard.SetText(vm.PreviewContent.Content);
    }

    private void OnPrevMatch(object sender, RoutedEventArgs e) => ScrollToMatch(-1);
    private void OnNextMatch(object sender, RoutedEventArgs e) => ScrollToMatch(1);

    private void ScrollToMatch(int direction)
    {
        var rtb = PreviewContentHost.Child as System.Windows.Controls.RichTextBox;
        if (rtb is null || _matchCount == 0) return;

        _matchIndex += direction;
        if (_matchIndex >= _matchCount) _matchIndex = 0;
        if (_matchIndex < 0) _matchIndex = _matchCount - 1;

        var match = 0;
        foreach (var block in rtb.Document.Blocks)
        {
            if (block is not Paragraph para) continue;
            foreach (var inline in para.Inlines)
            {
                if (inline is Run { Background: not null } && match++ == _matchIndex)
                {
                    // 获取 Run 的起始 TextPointer，滚动到该位置
                    var start = inline.ContentStart;
                    rtb.CaretPosition = start;
                    var rect = start.GetCharacterRect(System.Windows.Documents.LogicalDirection.Forward);
                    rtb.BringIntoView(rect);
                    UpdateMatchLabel();
                    return;
                }
            }
        }
    }

    private void UpdateMatchLabel()
    {
        MatchLabel.Text = _matchCount == 0 ? "" : $"{_matchIndex + 1}/{_matchCount}";
    }
}
