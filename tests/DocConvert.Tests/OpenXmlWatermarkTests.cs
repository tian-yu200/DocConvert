using DocumentFormat.OpenXml.Packaging;
using DocConvert.Core;
using DocConvert.Infrastructure.Windows;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace DocConvert.Tests;

public sealed class OpenXmlWatermarkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocConvert.OpenXml.Tests", Guid.NewGuid().ToString("N"));

    public OpenXmlWatermarkTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DocxHeaderWatermark_RequiresConfirmationAndIsRemoved()
    {
        var input = Path.Combine(_root, "word.docx");
        using (var document = WordprocessingDocument.Create(input, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("正文保留")))));
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text("机密"))));
            var section = main.Document.Body!.AppendChild(new W.SectionProperties());
            section.Append(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(header) });
            main.Document.Save();
        }

        var engine = new OpenXmlWatermarkEngine();
        var candidates = await engine.DetectAsync(input, null, CancellationToken.None);
        var candidate = Assert.Single(candidates);
        var output = Path.Combine(_root, "word_无水印.docx");
        var request = RemovalRequest(input, output, [candidate.Label]);
        var result = await engine.ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var cleaned = WordprocessingDocument.Open(output, false);
        var mainPart = cleaned.MainDocumentPart!;
        Assert.Equal("正文保留", mainPart.Document!.Body!.InnerText);
        Assert.DoesNotContain("机密", string.Concat(mainPart.HeaderParts.Select(part => part.Header!.InnerText)));
    }

    [Fact]
    public async Task XlsxHeaderWatermark_IsRemovedWithoutChangingCell()
    {
        var input = Path.Combine(_root, "sheet.xlsx");
        using (var document = SpreadsheetDocument.Create(input, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new X.Workbook(new X.Sheets());
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new X.Worksheet(
                new X.SheetData(new X.Row(new X.Cell { CellReference = "A1", DataType = X.CellValues.String, CellValue = new X.CellValue("保留值") })),
                new X.HeaderFooter(new X.OddHeader("&C机密")));
            ((X.Sheets)workbook.Workbook.Sheets!).Append(new X.Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Sheet1" });
            workbook.Workbook.Save();
        }

        var engine = new OpenXmlWatermarkEngine();
        var candidates = await engine.DetectAsync(input, null, CancellationToken.None);
        Assert.Contains(candidates, candidate => candidate.Label == "工作表页眉/页脚水印");
        var output = Path.Combine(_root, "sheet_无水印.xlsx");
        var result = await engine.ExecuteAsync(RemovalRequest(input, output, ["工作表页眉/页脚水印"]), null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var cleaned = SpreadsheetDocument.Open(output, false);
        var sheet = cleaned.WorkbookPart!.WorksheetParts.Single().Worksheet!;
        Assert.Empty(sheet.Elements<X.HeaderFooter>());
        Assert.Equal("保留值", sheet.Descendants<X.CellValue>().Single().Text);
    }

    [Fact]
    public async Task NoWatermarkDocument_HasNoFalsePositive()
    {
        var input = Path.Combine(_root, "clean.docx");
        using (var document = WordprocessingDocument.Create(input, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("普通文档")))));
            main.Document.Save();
        }
        Assert.Empty(await new OpenXmlWatermarkEngine().DetectAsync(input, null, CancellationToken.None));
    }

    private static DocumentJobRequest RemovalRequest(string input, string output, IReadOnlyList<string> labels) => new()
    {
        JobId = Guid.NewGuid(),
        Kind = JobKind.RemoveWatermark,
        InputPath = input,
        OutputPath = output,
        Watermark = new WatermarkOptions { ConfirmedCandidateLabels = labels }
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
