namespace WszSearcher.Core.Preview;

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
}

public enum PreviewType
{
    Text,
    Code,
    RichText,
    Image,
    Unsupported
}
