using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>索引进度（文件计数）</summary>
    public event Action<int>? ProgressChanged;

    /// <summary>当前索引中的文档总数</summary>
    public int DocCount { get; set; }

    /// <summary>索引是否就绪（volatile 确保跨线程可见）</summary>
    public volatile bool IsReady;

    /// <summary>索引目录路径</summary>
    public string IndexPath => _indexPath;

    public ContentIndexer(string? indexPath = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".",
            "Index");

        // 清理上次异常退出残留的写锁
        CleanStaleLock();

        System.IO.Directory.CreateDirectory(_indexPath);
        _parsers = new ParserRegistry();
        _directory = FSDirectory.Open(_indexPath);
        _analyzer = new JiebaAnalyzer();
        Debug.WriteLine($"[ContentIndexer] 索引路径: {_indexPath}");
    }

    private void CleanStaleLock()
    {
        try
        {
            var lockFile = Path.Combine(_indexPath, "write.lock");
            if (System.IO.File.Exists(lockFile))
            {
                System.IO.File.Delete(lockFile);
                Debug.WriteLine($"[ContentIndexer] 清理残留 write.lock");
            }
        }
        catch { }
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

    /// <summary>全量重建索引（并行处理，末尾单次 Commit）</summary>
    public async Task BuildFullIndexAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        IsReady = false;
        var writer = GetWriter();

        writer.DeleteAll();
        writer.Commit();

        var handler = StatusChanged;
        handler?.Invoke("正在建立内容索引...");
        var sw = Stopwatch.StartNew();

        var files = filePaths.ToList();
        var count = 0;
        const int reportInterval = 50;

        await Task.Run(() =>
        {
            Parallel.ForEach(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = ct
            }, filePath =>
            {
                ct.ThrowIfCancellationRequested();
                try { IndexFileInternal(writer, filePath, skipDelete: true, ct); } catch { }
                var n = Interlocked.Increment(ref count);
                if (n % reportInterval == 0)
                {
                    DocCount = n; // 实时更新文档计数
                    var mh = StatusChanged; mh?.Invoke($"内容索引中... 已处理 {n} 个文件");
                    var ph = ProgressChanged; ph?.Invoke(n);
                }
            });
        }, ct);

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
        => IndexFileInternal(writer, filePath, skipDelete: false, ct);

    private void IndexFileInternal(IndexWriter writer, string filePath, bool skipDelete, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var text = _parsers.ExtractTextAsync(filePath, ct).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) return;

            var doc = new Document();
            doc.Add(new Field("path", filePath, Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("filename", Path.GetFileName(filePath), Field.Store.YES, Field.Index.ANALYZED));
            doc.Add(new Field("extension", Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                Field.Store.YES, Field.Index.NOT_ANALYZED));
            doc.Add(new Field("content", text, Field.Store.NO, Field.Index.ANALYZED));

            // 拼音字段：仅对含中文的文本转换
            if (PinyinHelper.ContainsChinese(text))
            {
                var py = PinyinHelper.GetFirstLetters(text);
                var fullPy = PinyinHelper.GetPinyin(text);
                var pinyin = string.IsNullOrEmpty(fullPy) ? py : $"{py} {fullPy}";
                if (pinyin.Length > 0)
                    doc.Add(new Field("pinyin", pinyin, Field.Store.NO, Field.Index.ANALYZED));
            }

            if (!skipDelete)
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
            DocCount = writer.NumDocs();
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
            var exists = IndexReader.IndexExists(_directory);
            Debug.WriteLine($"[IndexExists] 路径={_indexPath}, 目录存在={System.IO.Directory.Exists(_indexPath)}, 索引={exists}");
            if (!exists && System.IO.Directory.Exists(_indexPath))
            {
                // 目录存在但 Lucene 认为无索引，列出文件帮助诊断
                foreach (var f in System.IO.Directory.GetFiles(_indexPath).Take(10))
                    Debug.WriteLine($"  {System.IO.Path.GetFileName(f)}");
            }
            return exists;
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[IndexExists] IOException: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[IndexExists] UnauthorizedAccess: {ex.Message}");
            return false;
        }
    }

    /// <summary>尝试从磁盘读取索引文档数（不依赖内存 IsReady 状态）</summary>
    public int TryGetDocCount()
    {
        try
        {
            if (!IndexReader.IndexExists(_directory)) return 0;
            using var reader = IndexReader.Open(_directory, true);
            return reader.NumDocs();
        }
        catch { return 0; }
    }

    /// <summary>从磁盘同步 DocCount</summary>
    public void SyncDocCount()
    {
        DocCount = TryGetDocCount();
    }

    /// <summary>提交增量变更（增量索引后调用）</summary>
    public void CommitChanges()
    {
        try
        {
            lock (_writerLock)
            {
                if (_writer is not null && !_writerClosed)
                {
                    _writer.Commit();
                    DocCount = _writer.NumDocs();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Commit 失败: {ex.Message}");
        }
    }

    /// <summary>关闭 IndexWriter，释放内存。下次写入时自动重新打开</summary>
    public void CloseWriter()
    {
        lock (_writerLock)
        {
            if (_writer is null || _writerClosed) return;
            _writer.Dispose();
            _writer = null;
            _writerClosed = true;
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
