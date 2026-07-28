using System.Collections.Concurrent;

namespace WszSearcher.Core.FileNameSearch;

/// <summary>内存文件名索引——支持毫秒级前缀/包含/模糊搜索</summary>
public class FileNameIndex : IDisposable
{
    // 按全路径索引（去重用）
    private readonly ConcurrentDictionary<string, FileRecord> _byPath = new(StringComparer.OrdinalIgnoreCase);
    // 按文件名索引（便于搜索）
    private readonly List<FileRecord> _allFiles = [];
    private readonly ReaderWriterLockSlim _lock = new();

    private volatile bool _isReady;
    private volatile int _count; // volatile 确保跨线程可见性

    /// <summary>索引是否就绪</summary>
    public bool IsReady => _isReady;
    /// <summary>索引文件总数</summary>
    public int Count => _count;

    /// <summary>统计在指定路径范围内的文件数</summary>
    public int CountInPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;
        _lock.EnterReadLock();
        try
        {
            var ps = paths.Select(p => p.EndsWith('\\') ? p : p + "\\").ToList();
            return _allFiles.Count(f => ps.Any(p =>
                f.FullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>批量添加记录（首次建索引时使用）</summary>
    public void AddRange(IEnumerable<FileRecord> records)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.FullPath)) continue;
                _byPath[record.FullPath] = record;
                _allFiles.Add(record);
            }
            _count = _allFiles.Count;
            _isReady = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>添加或更新单条记录</summary>
    public void AddOrUpdate(FileRecord record)
    {
        if (string.IsNullOrEmpty(record.FullPath)) return;

        _lock.EnterWriteLock();
        try
        {
            if (_byPath.TryGetValue(record.FullPath, out var existing))
            {
                // 更新（保持引用一致，但简单实现直接替换）
                _allFiles.Remove(existing);
                _byPath[record.FullPath] = record;
                _allFiles.Add(record);
            }
            else
            {
                _byPath[record.FullPath] = record;
                _allFiles.Add(record);
                _count++;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>删除记录</summary>
    public bool Remove(string fullPath)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_byPath.TryRemove(fullPath, out var existing))
            {
                _allFiles.Remove(existing);
                _count--;
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>搜索文件名（仅在指定路径范围内）</summary>
    public List<FileRecord> Search(string query, IReadOnlyList<string> paths, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || !_isReady)
            return [];

        List<FileRecord> snapshot;
        _lock.EnterReadLock();
        try
        {
            if (paths.Count == 0)
                snapshot = new List<FileRecord>(_allFiles);
            else
            {
                var ps = paths.Select(p => p.EndsWith('\\') ? p : p + "\\").ToList();
                snapshot = _allFiles.Where(f => ps.Any(p =>
                    f.FullPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
            }
        }
        finally { _lock.ExitReadLock(); }

        return SearchCore(query, snapshot, maxResults);
    }

    /// <summary>搜索文件名（全盘，向后兼容）</summary>
    public List<FileRecord> Search(string query, int maxResults = 50)
    {
        return Search(query, Array.Empty<string>(), maxResults);
    }

    private List<FileRecord> SearchCore(string query, List<FileRecord> snapshot, int maxResults)
    {

        // 在快照上搜索，不持有锁
        var q = query.Trim();
        var phaseMax = maxResults / 3; // 前 3 阶段只占 1/3，给拼音留 2/3
        var results = new List<FileRecord>(maxResults);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Phase 1: 前缀匹配
        foreach (var record in snapshot)
        {
            if (results.Count >= phaseMax) break;
            if (record.FileName.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(record.FullPath))
                    results.Add(record);
            }
        }

        // Phase 2: 包含匹配
        if (results.Count < phaseMax)
        {
            foreach (var record in snapshot)
            {
                if (results.Count >= phaseMax) break;
                if (seen.Contains(record.FullPath)) continue;
                if (record.FileName.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(record.FullPath))
                        results.Add(record);
                }
            }
        }

        // Phase 3: 路径包含匹配
        if (results.Count < phaseMax)
        {
            foreach (var record in snapshot)
            {
                if (results.Count >= phaseMax) break;
                if (seen.Contains(record.FullPath)) continue;
                if (record.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(record.FullPath))
                        results.Add(record);
                }
            }
        }

        // Phase 4: 拼音首字母 + 全拼匹配（仅纯 ASCII 查询）
        if (results.Count < maxResults && IsAscii(q))
        {
            foreach (var record in snapshot)
            {
                if (results.Count >= maxResults) break;
                if (seen.Contains(record.FullPath)) continue;
                if ((record.NamePinyin.Length > 0 && record.NamePinyin.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                    (record.NameFullPinyin.Length > 0 && record.NameFullPinyin.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    if (seen.Add(record.FullPath))
                        results.Add(record);
                }
            }
        }

        return results;
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s) if (c > 127) return false;
        return true;
    }

    /// <summary>清空索引</summary>
    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _byPath.Clear();
            _allFiles.Clear();
            _count = 0;
            _isReady = false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
