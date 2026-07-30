namespace WszSearcher.Core.Preview;

/// <summary>预览服务接口</summary>
public interface IPreviewService
{
    /// <summary>异步获取文件预览（keyword 用于后台预处理高亮分段，避免 UI 线程字符串搜索）</summary>
    Task<PreviewResult> GetPreviewAsync(string filePath, string? keyword = null, CancellationToken ct = default);
}
