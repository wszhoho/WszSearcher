namespace WszSearcher.Core.Preview;

/// <summary>预览服务接口</summary>
public interface IPreviewService
{
    /// <summary>异步获取文件预览</summary>
    Task<PreviewResult> GetPreviewAsync(string filePath, CancellationToken ct = default);
}
