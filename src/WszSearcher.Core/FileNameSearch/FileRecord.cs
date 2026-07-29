namespace WszSearcher.Core.FileNameSearch;

/// <summary>文件记录——从 USN Journal 扫描得到的文件信息</summary>
public class FileRecord
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public ulong FileReferenceNumber { get; set; }
    public ulong ParentFileReferenceNumber { get; set; }
    public bool IsDirectory { get; set; }
    /// <summary>文件名拼音首字母，用于拼音搜索</summary>
    public string NamePinyin { get; set; } = string.Empty;
    /// <summary>文件名拼音全拼</summary>
    public string NameFullPinyin { get; set; } = string.Empty;

    /// <summary>目录路径（从 FullPath 计算）</summary>
    public string Directory => System.IO.Path.GetDirectoryName(FullPath) ?? string.Empty;

    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();

    public override string ToString() => FullPath;
}
