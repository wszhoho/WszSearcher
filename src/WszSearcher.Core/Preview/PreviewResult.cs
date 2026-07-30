namespace WszSearcher.Core.Preview;

/// <summary>高亮分段——后台线程预处理，UI 线程直接遍历构建 Run 元素</summary>
/// <param name="Text">分段文本</param>
/// <param name="IsHighlight">是否为搜索词匹配段</param>
public readonly record struct HighlightSegment(string Text, bool IsHighlight);

/// <summary>预览结果</summary>
public class PreviewResult
{
    /// <summary>预览文本内容</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>内容类型</summary>
    public PreviewType Type { get; set; } = PreviewType.Text;

    /// <summary>图片路径（仅 Type=Image 时有效）</summary>
    public string? ImagePath { get; set; }

    /// <summary>标题（文件名）</summary>
    public string? Title { get; set; }

    /// <summary>文件路径</summary>
    public string? FilePath { get; set; }

    /// <summary>高亮分段列表（后台预处理，避免 UI 线程字符串搜索）</summary>
    public List<HighlightSegment>? HighlightSegments { get; set; }
}

public enum PreviewType
{
    Text,
    Code,
    RichText,
    Image,
    Unsupported
}
