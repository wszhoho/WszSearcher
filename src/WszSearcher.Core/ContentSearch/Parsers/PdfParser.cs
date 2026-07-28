using UglyToad.PdfPig;

namespace WszSearcher.Core.ContentSearch.Parsers;

/// <summary>PDF 文档解析器——使用 PdfPig 提取文本</summary>
public class PdfParser : IDocumentParser
{
    private static readonly HashSet<string> Supported = ["pdf"];

    public bool CanParse(string extension)
        => Supported.Contains(extension);

    public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            using var pdf = PdfDocument.Open(filePath);
            var text = new System.Text.StringBuilder();

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                text.AppendLine(page.Text);
            }

            return Task.FromResult<string?>(text.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PDF 解析失败 [{filePath}]: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }
}
