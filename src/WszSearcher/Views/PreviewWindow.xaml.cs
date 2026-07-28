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
        var keyword = vm.SearchText?.Trim();

        // 图片类型不渲染文本
        if (content.Type == PreviewType.Image)
        {
            PreviewContentHost.Child = new System.Windows.Controls.Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(content.ImagePath ?? content.FilePath)),
                MaxWidth = 360, MaxHeight = 500, Stretch = System.Windows.Media.Stretch.Uniform
            };
            return;
        }

        // 构建 RichTextBox（默认文本样式）
        var rtb = new System.Windows.Controls.RichTextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = TryFindResource("TextPrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black
        };
        rtb.Resources.Add(typeof(Paragraph), new Style(typeof(Paragraph)) { Setters = { new Setter(Paragraph.MarginProperty, new Thickness(0)) } });

        // 根据类型设置样式
        switch (content.Type)
        {
            case PreviewType.Code:
                rtb.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
                rtb.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4));
                rtb.FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New");
                rtb.FontSize = 12;
                break;
            case PreviewType.RichText:
                rtb.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF8, 0xE1));
                rtb.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
                rtb.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE0, 0x82));
                rtb.BorderThickness = new Thickness(1);
                break;
        }

        PreviewContentHost.Child = rtb;

        // 高亮搜索词
        var para = new Paragraph();
        _matchIndex = -1;
        _matchCount = 0;

        if (string.IsNullOrEmpty(keyword) || text.Length < keyword.Length)
        {
            para.Inlines.Add(new Run(text));
        }
        else
        {
            var idx = 0;
            while (idx < text.Length)
            {
                var pos = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase);
                if (pos < 0)
                {
                    para.Inlines.Add(new Run(text[idx..]));
                    break;
                }
                if (pos > idx)
                    para.Inlines.Add(new Run(text[idx..pos]));
                para.Inlines.Add(new Run(text[pos..(pos + keyword.Length)])
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xE4, 0xB3)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A))
                });
                _matchCount++;
                idx = pos + keyword.Length;
            }
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
