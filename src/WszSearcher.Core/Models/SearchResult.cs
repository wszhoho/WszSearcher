namespace WszSearcher.Core.Models;

/// <summary>搜索结果数据模型</summary>
public class SearchResult
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public SearchResultType ResultType { get; set; }
    public string MatchSnippet { get; set; } = string.Empty; // 内容匹配片段（高亮）
    public double Score { get; set; }

    /// <summary>文件扩展名（小写，不含点）</summary>
    public string Extension => Path.GetExtension(FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
}

public enum SearchResultType
{
    /// <summary>文件名匹配</summary>
    FileName,
    /// <summary>文件内容匹配</summary>
    Content
}
