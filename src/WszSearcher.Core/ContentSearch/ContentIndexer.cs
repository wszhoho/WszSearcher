using System.Diagnostics;
using WszSearcher.Core.ContentSearch.Parsers;
using WszSearcher.Core.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using LuceneDirectory = Lucene.Net.Store.Directory;

namespace WszSearcher.Core.ContentSearch;

/// <summary>
/// 内容索引器——使用 Lucene.NET 建立全文倒排索引
/// </summary>
public class ContentIndexer : IDisposable
{
    private readonly string _indexPath;
    private readonly ParserRegistry _parsers;
    private readonly FSDirectory _directory;
    private readonly JiebaAnalyzer _analyzer;
    private readonly object _writerLock = new(); // 保护 _writer 并发访问
    private IndexWriter? _writer;
    private bool _writerClosed;
    private bool _disposed;

    /// <summary>索引状态事件</summary>
    public event Action<string>? StatusChanged;

    /// <summary>当前索引中的文档总数</summary>
    public int DocCount { get; private set; }

    /// <summary>索引是否就绪（volatile 确保跨线程可见）</summary>
    public volatile bool IsReady;

    /// <summary>索引目录路径</summary>
    public string IndexPath => _indexPath;

    public ContentIndexer(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            AppContext.BaseDirectory, "Index");

        System.IO.Directory.CreateDirectory(_indexPath);
        _parsers = new ParserRegistry();
        _directory = FSDirectory.Open(_indexPath);
        _analyzer = new JiebaAnalyzer();
    }

    /// <summary>打开或创建 IndexWriter（线程安全）</summary>
    private IndexWriter GetWriter()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ContentIndexer));

        lock (_writerLock)
        {
            if (_writer is null || _writerClosed)
            {
                _writerClosed = false;
                var create = !IndexReader.IndexExists(_directory);
                _writer = new IndexWriter(_directory, _analyzer, create, IndexWriter.MaxFieldLength.UNLIMITED);
            }
            return _writer;
        }
    }

    /// <summary>全量重建索引（定期 commit 防止内存膨胀）</summary>
    public async Task BuildFullIndexAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        IsReady = false;
        var writer = GetWriter();

        // 清空旧索引
        writer.DeleteAll();
        writer.Commit();

        var handler = StatusChanged;
        handler?.Invoke("正在建立内容索引...");
        var sw = Stopwatch.StartNew();
        var count = 0;
        const int commitBatchSize = 500; // 每 500 个文件 commit 一次，释放 Lucene 内存

        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await IndexFileAsync(writer, filePath, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"索引跳过 [{filePath}]: {ex.Message}");
            }
            count++;

            // 定期 commit 减少内存压力
            if (count % commitBatchSize == 0)
            {
                writer.Commit();
                handler = StatusChanged;
                handler?.Invoke($"内容索引中... 已处理 {count} 个文件");
            }
        }

        writer.Commit();
        DocCount = writer.NumDocs();
        IsReady = true;
        sw.Stop();

        var finalHandler = StatusChanged;
        finalHandler?.Invoke($"内容索引完成！共 {DocCount} 个文档，耗时 {sw.Elapsed.TotalSeconds:F1} 秒");
    }

    /// <summary>增量索引单个文件</summary>
    public async Task IndexFileAsync(string filePath, CancellationToken ct = default)
    {
        await IndexFileAsync(GetWriter(), filePath, ct);
    }

    private async Task IndexFileAsync(IndexWriter writer, string filePath, CancellationToken ct)
    {
        try
        {
            var text = await _parsers.ExtractTextAsync(filePath, ct);
            if (string.IsNullOrWhiteSpace(text)) return;

            // 先构建文档，再原子性地删除旧记录 + 添加新记录，避免异常导致删除被孤立
            var doc = new Document();
            doc.Add(new Field("path", filePath, Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("filename", Path.GetFileName(filePath), Field.Store.YES, Field.Index.ANALYZED));
            doc.Add(new Field("extension", Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("content", text, Field.Store.NO, Field.Index.ANALYZED));

            // 原子操作：删除旧文档 + 添加新文档
            writer.DeleteDocuments(new Term("path", filePath));
            writer.AddDocument(doc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"索引失败 [{filePath}]: {ex.Message}");
        }
    }

    /// <summary>从索引中删除文件</summary>
    public void RemoveFile(string filePath)
    {
        try
        {
            var writer = GetWriter();
            writer.DeleteDocuments(new Term("path", filePath));
            writer.Commit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"删除文件索引失败 [{filePath}]: {ex.Message}");
        }
    }

    /// <summary>优化索引</summary>
    public void Optimize()
    {
        try
        {
            var writer = GetWriter();
            writer.Optimize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"索引优化失败: {ex.Message}");
        }
    }

    /// <summary>检查索引是否存在且有效</summary>
    public bool IndexExists()
    {
        try
        {
            return IndexReader.IndexExists(_directory);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_writerLock)
        {
            _writer?.Dispose();
            _writer = null;
            _writerClosed = true;
        }
        _analyzer.Dispose();
        _directory.Dispose();
    }
}
