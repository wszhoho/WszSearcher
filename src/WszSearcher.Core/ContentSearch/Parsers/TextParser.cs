using System.Text;

namespace WszSearcher.Core.ContentSearch.Parsers;

/// <summary>纯文本/代码文件解析器——处理 UTF-8/UTF-16/GBK 文本文件</summary>
public class TextParser : IDocumentParser
{
    /// <summary>索引文件大小上限（50MB），防止 OOM</summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    // 支持的文本文件扩展名
    private static readonly HashSet<string> TextExtensions =
    [
        "txt", "md", "markdown", "csv", "tsv", "log", "ini", "cfg", "conf",
        "json", "xml", "yaml", "yml", "toml", "config", "properties",
        "cs", "xaml", "js", "ts", "jsx", "tsx", "html", "htm", "css", "scss", "less",
        "py", "rb", "go", "rs", "java", "cpp", "c", "h", "hpp", "csproj", "sln",
        "sh", "bat", "ps1", "sql", "r", "m", "swift", "kt", "dart", "lua", "php",
        "pl", "pm", "tcl", "scala", "clj", "groovy", "gradle", "dockerfile",
        "makefile", "cmake", "proto", "graphql", "terraform", "tf",
        "env", "gitignore", "editorconfig", "nugetconfig", "props", "targets"
    ];

    public bool CanParse(string extension)
        => TextExtensions.Contains(extension);

    public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var content = ReadFileWithAutoDetect(filePath);
            return Task.FromResult<string?>(content);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"文本解析失败 [{filePath}]: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    private static string? ReadFileWithAutoDetect(string filePath)
    {
        // 跳过超大文件，防止 OOM
        var fi = new FileInfo(filePath);
        if (fi.Length > MaxFileSizeBytes)
        {
            System.Diagnostics.Debug.WriteLine($"文本解析跳过（文件过大 {fi.Length} 字节）: {filePath}");
            return null;
        }

        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length == 0) return null;

        // 检测 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 尝试 UTF-8（不会抛异常，非法字节会被替换为 U+FFFD）
        var utf8Text = Encoding.UTF8.GetString(bytes);

        // 如果替换字符过多（>1%），则用 GBK 重新解码（适合中文 Windows 环境）
        var replacementCount = utf8Text.Count(c => c == '\uFFFD');
        if (replacementCount > bytes.Length / 100)
        {
            try
            {
                return Encoding.GetEncoding("GBK").GetString(bytes);
            }
            catch
            {
                // GBK 不可用或解码失败，退回 UTF-8 结果
            }
        }

        return utf8Text;
    }
}
