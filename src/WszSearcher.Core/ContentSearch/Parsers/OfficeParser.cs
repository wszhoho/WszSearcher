using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Presentation;

namespace WszSearcher.Core.ContentSearch.Parsers;

/// <summary>Office 文档解析器——处理 DOCX/XLSX/PPTX</summary>
public class OfficeParser : IDocumentParser
{
    private static readonly HashSet<string> Supported = ["docx", "xlsx", "pptx"];

    public bool CanParse(string extension)
        => Supported.Contains(extension);

    public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var text = ext switch
            {
                "docx" => ExtractFromDocx(filePath, ct),
                "xlsx" => ExtractFromXlsx(filePath, ct),
                "pptx" => ExtractFromPptx(filePath, ct),
                _ => null
            };
            return Task.FromResult(text);
        }
        catch (DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            // 加密/损坏的 Office 文档，静默跳过
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Office 解析失败 [{filePath}]: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    private static string? ExtractFromDocx(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return null;

        return string.Join("\n",
            body.Descendants<Paragraph>()
                .Select(p => p.InnerText));
    }

    private static string? ExtractFromXlsx(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var workbook = SpreadsheetDocument.Open(filePath, false);
        var sb = new System.Text.StringBuilder();

        foreach (var sheetPart in workbook.WorkbookPart?.WorksheetParts ?? [])
        {
            ct.ThrowIfCancellationRequested(); // 每张工作表检查取消
            var sheetData = sheetPart.Worksheet?.GetFirstChild<SheetData>();
            if (sheetData is null) continue;

            foreach (var row in sheetData.Descendants<Row>())
            {
                var line = string.Join("\t",
                    row.Descendants<Cell>().Select(c => c.InnerText));
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private static string? ExtractFromPptx(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var presentation = PresentationDocument.Open(filePath, false);
        var sb = new System.Text.StringBuilder();

        foreach (var slidePart in presentation.PresentationPart?.SlideParts ?? [])
        {
            ct.ThrowIfCancellationRequested(); // 每张幻灯片检查取消
            var slide = slidePart.Slide;
            if (slide is null) continue;

            foreach (var text in slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
            {
                sb.AppendLine(text.Text);
            }
        }

        return sb.ToString();
    }
}
