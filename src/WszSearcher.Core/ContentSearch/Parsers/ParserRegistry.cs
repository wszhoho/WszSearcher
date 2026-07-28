namespace WszSearcher.Core.ContentSearch.Parsers;

/// <summary>解析器注册表——根据扩展名路由到对应的解析器</summary>
public class ParserRegistry
{
    private readonly List<IDocumentParser> _parsers = [];

    public ParserRegistry()
    {
        // 注册所有解析器（顺序重要：先注册的优先匹配）
        Register(new TextParser());
        Register(new PdfParser());
        Register(new OfficeParser());
    }

    public void Register(IDocumentParser parser) => _parsers.Add(parser);

    /// <summary>根据文件扩展名获取合适的解析器</summary>
    public IDocumentParser? GetParser(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return _parsers.FirstOrDefault(p => p.CanParse(ext));
    }

    /// <summary>是否支持该文件类型</summary>
    public bool CanParse(string filePath) => GetParser(filePath) is not null;

    /// <summary>从文件中提取文本</summary>
    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var parser = GetParser(filePath);
        if (parser is null) return null;
        return await parser.ExtractTextAsync(filePath, ct);
    }
}
