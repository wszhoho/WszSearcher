namespace WszSearcher.Core.ContentSearch.Parsers;

/// <summary>文档解析器接口——从文件中提取纯文本内容供索引</summary>
public interface IDocumentParser
{
    /// <summary>是否支持该文件扩展名</summary>
    bool CanParse(string extension);

    /// <summary>从文件中提取文本内容</summary>
    Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default);
}
