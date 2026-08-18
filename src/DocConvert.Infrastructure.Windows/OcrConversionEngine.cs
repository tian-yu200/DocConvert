using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocConvert.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Tesseract;

namespace DocConvert.Infrastructure.Windows;

public sealed class OcrConversionEngine : IConversionEngine
{
    private readonly OfficeConversionEngine _office = new();
    public string Name => "Tesseract OCR 引擎";

    public bool CanHandle(DocumentJobRequest request)
    {
        if (request.Kind != JobKind.Convert || !request.Conversion.EnableOcr) return false;
        return Path.GetExtension(request.InputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            && (Path.GetExtension(request.OutputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(request.OutputPath).Equals(".docx", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        using var workspace = new JobWorkspace(request.JobId);
        var searchable = workspace.PathFor("searchable.pdf");
        await Task.Run(() => CreateSearchablePdf(request, searchable, progress, cancellationToken), cancellationToken);

        if (Path.GetExtension(request.OutputPath).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var officeRequest = request with { InputPath = searchable, Conversion = request.Conversion with { EnableOcr = false } };
            return await _office.ExecuteAsync(officeRequest, progress, cancellationToken);
        }

        workspace.Commit(searchable, request.OutputPath);
        return JobResult.Ok(request.OutputPath);
    }

    private static void CreateSearchablePdf(DocumentJobRequest request, string outputPath, IProgress<JobProgress>? progress, CancellationToken token)
    {
        PdfFontService.EnsureInitialized();
        var tessdata = AppPaths.FindTessdata();
        AppPaths.RequireOcrLanguages(tessdata, request.Conversion.OcrLanguages);
        var pages = PdfRenderingService.Render(request.InputPath, Path.Combine(Path.GetDirectoryName(outputPath)!, "ocr-pages"), 300, token);
        using var engine = new TesseractEngine(tessdata, request.Conversion.OcrLanguages, EngineMode.LstmOnly);
        using var output = new PdfDocument();

        for (var index = 0; index < pages.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var rendered = pages[index];
            using var pix = Pix.LoadFromFile(rendered.ImagePath);
            using var result = engine.Process(pix, PageSegMode.Auto);
            using var image = XImage.FromFile(rendered.ImagePath);
            var pdfPage = output.AddPage();
            pdfPage.Width = XUnit.FromPoint(rendered.WidthPoints);
            pdfPage.Height = XUnit.FromPoint(rendered.HeightPoints);
            using var graphics = XGraphics.FromPdfPage(pdfPage);
            graphics.DrawImage(image, 0, 0, pdfPage.Width.Point, pdfPage.Height.Point);

            using var iterator = result.GetIterator();
            iterator.Begin();
            do
            {
                var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (string.IsNullOrWhiteSpace(text) || !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box)) continue;
                var x = box.X1 * rendered.WidthPoints / pix.Width;
                var y = box.Y1 * rendered.HeightPoints / pix.Height;
                var width = Math.Max(2, (box.X2 - box.X1) * rendered.WidthPoints / pix.Width);
                var height = Math.Max(5, (box.Y2 - box.Y1) * rendered.HeightPoints / pix.Height);
                var font = new XFont(ContainsCjk(text) ? "Microsoft YaHei" : "Arial", Math.Max(5, height * 0.75));
                var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
                graphics.DrawString(text, font, invisible, new XRect(x, y, width, height), XStringFormats.TopLeft);
            } while (iterator.Next(PageIteratorLevel.Word));

            progress?.Report(new JobProgress((index + 1d) / pages.Count * 95, $"正在 OCR 第 {index + 1}/{pages.Count} 页"));
        }

        output.Save(outputPath);
    }

    private static bool ContainsCjk(string text) => text.Any(character => character >= 0x3400 && character <= 0x9fff);
}
