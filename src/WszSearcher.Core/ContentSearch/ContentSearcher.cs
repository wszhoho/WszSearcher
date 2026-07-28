using WszSearcher.Core.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using WszSearcher.Core.Models;

namespace WszSearcher.Core.ContentSearch;

/// <summary>内容搜索器——查询 Lucene.NET 全文索引（支持中文分词）</summary>
public class ContentSearcher : IDisposable
{
    private readonly string _indexPath;
    private readonly FSDirectory _directory;
    private readonly JiebaAnalyzer _analyzer;
    private bool _disposed;

    public bool IsIndexReady { get; private set; }

    public ContentSearcher(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            AppContext.BaseDirectory, "Index");

        _directory = FSDirectory.Open(_indexPath);
        _analyzer = new JiebaAnalyzer();
    }

    /// <summary>刷新索引就绪状态（由外部在索引构建完成后调用）</summary>
    public void RefreshReadyState()
    {
        IsIndexReady = IndexReader.IndexExists(_directory);
    }

    /// <summary>搜索内容索引（文件名 + 文件内容，支持中文搜索）</summary>
    public List<SearchResult> Search(string query, int maxResults = 30)
    {
        // 每次搜索时动态检查索引是否存在
        if (!IndexReader.IndexExists(_directory) || string.IsNullOrWhiteSpace(query))
            return [];

        var results = new List<SearchResult>();

        try
        {
            using var reader = IndexReader.Open(_directory, true);
            var searcher = new IndexSearcher(reader);

            // 搜索文件名和内容两个字段（JiebaAnalyzer 会自动进行中文分词）
            var queryParser = new MultiFieldQueryParser(
                Lucene.Net.Util.Version.LUCENE_30,
                ["filename", "content"],
                _analyzer);

            // 转义特殊字符
            var escapedQuery = QueryParser.Escape(query);

            // 执行搜索
            var luceneQuery = queryParser.Parse(escapedQuery);
            var hits = searcher.Search(luceneQuery, null, maxResults, Sort.RELEVANCE);

            foreach (var hit in hits.ScoreDocs)
            {
                var doc = searcher.Doc(hit.Doc);
                var filePath = doc.Get("path");
                // 先检查 filePath 是否为 null 再调用 Path.GetFileName
                if (string.IsNullOrEmpty(filePath)) continue;

                var fileName = doc.Get("filename") ?? Path.GetFileName(filePath);

                results.Add(new SearchResult
                {
                    FileName = fileName,
                    FullPath = filePath,
                    Directory = Path.GetDirectoryName(filePath) ?? "",
                    ResultType = SearchResultType.Content,
                    MatchSnippet = $"[内容匹配] 相关度: {hit.Score:F2}",
                    Score = hit.Score
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"内容搜索失败: {ex.Message}");
        }

        return results;
    }

    /// <summary>检查索引是否存在</summary>
    public static bool CheckIndexExists(string? indexPath = null)
    {
        indexPath ??= Path.Combine(
            AppContext.BaseDirectory, "Index");

        try
        {
            using var dir = FSDirectory.Open(indexPath);
            return IndexReader.IndexExists(dir);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analyzer.Dispose();
        _directory.Dispose();
    }
}
