using WszSearcher.Core.ContentSearch.Parsers;
using WszSearcher.Core.Localization;

namespace WszSearcher.Core.Preview;

/// <summary>
/// 预览服务——使用 P3 的文档解析器实现真实文件预览
/// 支持：文本/代码、PDF、Office 文档、图片、HTML
/// </summary>
public class PreviewService : IPreviewService
{
    // 限制预览文件大小（超过 100MB 只显示提示，防止 OOM）
    private const long MaxPreviewSize = 100 * 1024 * 1024;
    // 限制预览文本行数（减少 UI 线程 RichTextBox 渲染压力）
    private const int MaxPreviewLines = 1000;

    private readonly ParserRegistry _parsers;

    public PreviewService()
    {
        _parsers = new ParserRegistry();
    }

    public async Task<PreviewResult> GetPreviewAsync(string filePath, string? keyword = null, CancellationToken ct = default)
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
                    StatusKey = StatusKeys.PreviewFileNotFound,
                    StatusArgs = [fileName],
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
                    StatusKey = StatusKeys.PreviewFileTooLarge,
                    StatusArgs = [FormatSize(fileInfo.Length)],
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
                _ => await TextBasedPreviewAsync(filePath, fileName, ext, keyword, ct)
            };
        }
        catch (OperationCanceledException)
        {
            return new PreviewResult
            {
                StatusKey = StatusKeys.PreviewCancelled,
                Type = PreviewType.Text,
                Title = fileName,
                FilePath = filePath
            };
        }
        catch (Exception ex)
        {
            return new PreviewResult
            {
                StatusKey = StatusKeys.PreviewLoadFailed,
                StatusArgs = [ex.Message],
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
        string filePath, string fileName, string ext, string? keyword, CancellationToken ct)
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
                StatusKey = StatusKeys.PreviewBinaryNotSupported,
                StatusArgs = [fileName],
                Type = PreviewType.Text,
                Title = fileName,
                FilePath = filePath
            };
        }

        // 去除连续空行（不保留空行）
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{2,}", "\n");

        // 限制行数（若关键词在截断区之后，则以关键词行为中心保留窗口，保证高亮可见）
        // 截断提示行不在此拼接（保持文本纯净），改由 PreviewResult 携带截断元数据，UI 层按当前语言追加提示
        var lines = text.Split('\n');
        int? truncatedTotal = null;
        int? truncatedSkip = null;
        if (lines.Length > MaxPreviewLines)
        {
            int keywordLine = -1;
            if (!string.IsNullOrEmpty(keyword))
            {
                keywordLine = Array.FindIndex(lines, l =>
                    l.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (keywordLine < 0 || keywordLine < MaxPreviewLines)
            {
                // 无关键词，或关键词本就在前 MaxPreviewLines 行内——只保留前 MaxPreviewLines 行
                text = string.Join('\n', lines.Take(MaxPreviewLines));
                truncatedTotal = lines.Length;
                truncatedSkip = 0;
            }
            else
            {
                // 关键词在截断区之后——以关键词行为中心保留 MaxPreviewLines 行窗口
                int start = keywordLine - MaxPreviewLines / 2;
                start = Math.Max(0, Math.Min(start, lines.Length - MaxPreviewLines));
                var window = lines[start..(start + MaxPreviewLines)];
                text = string.Join('\n', window);
                truncatedTotal = lines.Length;
                truncatedSkip = start;
            }
        }

        return new PreviewResult
        {
            Content = text,
            Type = type,
            Title = fileName,
            FilePath = filePath,
            TruncatedTotalLines = truncatedTotal,
            TruncatedSkipLines = truncatedSkip,
            HighlightSegments = BuildHighlightSegments(text, keyword) // 后台线程预处理高亮分段
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    /// <summary>在后台线程预处理高亮分段——将文本按关键词匹配拆分为普通/高亮片段列表</summary>
    /// <param name="text">原始文本</param>
    /// <param name="keyword">搜索关键词（null 或空时返回 null）</param>
    /// <returns>高亮分段列表，无关键词时返回 null 表示无需高亮</returns>
    private static List<HighlightSegment>? BuildHighlightSegments(string text, string? keyword)
    {
        if (string.IsNullOrEmpty(keyword) || string.IsNullOrEmpty(text) || text.Length < keyword.Length)
            return null;

        var segments = new List<HighlightSegment>();
        var idx = 0;
        while (idx < text.Length)
        {
            var pos = text.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                // 剩余全部为普通文本
                segments.Add(new HighlightSegment(text[idx..], false));
                break;
            }
            // 关键词前的普通文本
            if (pos > idx)
                segments.Add(new HighlightSegment(text[idx..pos], false));
            // 匹配的高亮文本
            segments.Add(new HighlightSegment(text[pos..(pos + keyword.Length)], true));
            idx = pos + keyword.Length;
        }
        return segments;
    }
}
