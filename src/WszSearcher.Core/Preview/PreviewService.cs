using WszSearcher.Core.ContentSearch.Parsers;

namespace WszSearcher.Core.Preview;

/// <summary>
/// 预览服务——使用 P3 的文档解析器实现真实文件预览
/// 支持：文本/代码、PDF、Office 文档、图片、HTML
/// </summary>
public class PreviewService : IPreviewService
{
    // 限制预览文件大小（超过 100MB 只显示提示，防止 OOM）
    private const long MaxPreviewSize = 100 * 1024 * 1024;
    // 限制预览文本行数
    private const int MaxPreviewLines = 5000;

    private readonly ParserRegistry _parsers;

    public PreviewService()
    {
        _parsers = new ParserRegistry();
    }

    public async Task<PreviewResult> GetPreviewAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var fileName = Path.GetFileName(filePath);

        try
        {
            // 检查文件是否存在
            if (!File.Exists(filePath))
            {
                return new PreviewResult
                {
                    Content = $"[文件不存在：{fileName}]",
                    Type = PreviewType.Text,
                    Title = fileName,
                    FilePath = filePath
                };
            }

            // 检查文件大小
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxPreviewSize)
            {
                return new PreviewResult
                {
                    Content = $"[文件过大（{FormatSize(fileInfo.Length)}），超过 10MB 限制，无法预览]",
                    Type = PreviewType.Text,
                    Title = fileName,
                    FilePath = filePath
                };
            }

            // 根据文件类型路由到不同预览器
            return ext switch
            {
                // 图片
                "png" or "jpg" or "jpeg" or "gif" or "bmp" or "svg" or "webp" or "ico"
                    => await ImagePreviewAsync(filePath, fileName, ct),

                // 使用文档解析器提取文本
                _ => await TextBasedPreviewAsync(filePath, fileName, ext, ct)
            };
        }
        catch (OperationCanceledException)
        {
            return new PreviewResult
            {
                Content = "[预览已取消]",
                Type = PreviewType.Text,
                Title = fileName,
                FilePath = filePath
            };
        }
        catch (Exception ex)
        {
            return new PreviewResult
            {
                Content = $"[预览失败：{ex.Message}]",
                Type = PreviewType.Text,
                Title = fileName,
                FilePath = filePath
            };
        }
    }

    /// <summary>图片预览</summary>
    private Task<PreviewResult> ImagePreviewAsync(string filePath, string fileName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new PreviewResult
        {
            Type = PreviewType.Image,
            ImagePath = filePath,
            Title = fileName,
            FilePath = filePath
        });
    }

    /// <summary>基于文本的预览（使用 ParserRegistry 解析各种文档格式）</summary>
    private async Task<PreviewResult> TextBasedPreviewAsync(
        string filePath, string fileName, string ext, CancellationToken ct)
    {
        PreviewType type = ext switch
        {
            "cs" or "xaml" or "js" or "ts" or "tsx" or "jsx"
            or "html" or "htm" or "css" or "scss" or "less"
            or "py" or "rb" or "go" or "rs" or "java"
            or "cpp" or "c" or "h" or "hpp" or "swift" or "kt"
            or "sql" or "sh" or "bat" or "ps1" or "php"
            or "pl" or "scala" or "groovy" or "gradle"
                => PreviewType.Code,

            "pdf" or "docx" or "xlsx" or "pptx"
                => PreviewType.RichText,

            _ => PreviewType.Text
        };

        // 通过 P3 的解析器提取文本
        var text = await _parsers.ExtractTextAsync(filePath, ct);

        if (string.IsNullOrEmpty(text))
        {
            return new PreviewResult
            {
                Content = $"[无法提取文件内容：{fileName}]",
                Type = PreviewType.Text,
                Title = fileName,
                FilePath = filePath
            };
        }

        // 去除连续空行（不保留空行）
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{2,}", "\n");

        // 限制行数
        var lines = text.Split('\n');
        if (lines.Length > MaxPreviewLines)
        {
            text = string.Join('\n', lines.Take(MaxPreviewLines))
                 + $"\n\n... (已截断，共 {lines.Length} 行，仅显示前 {MaxPreviewLines} 行)";
        }

        return new PreviewResult
        {
            Content = text,
            Type = type,
            Title = fileName,
            FilePath = filePath
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
