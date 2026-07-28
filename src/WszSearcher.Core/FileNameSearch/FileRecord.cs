namespace WszSearcher.Core.FileNameSearch;

/// <summary>文件记录——从 USN Journal 扫描得到的文件信息</summary>
public class FileRecord
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public ulong FileReferenceNumber { get; set; }
    public ulong ParentFileReferenceNumber { get; set; }
    public bool IsDirectory { get; set; }

    public string Extension => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();

    public override string ToString() => FullPath;
}
