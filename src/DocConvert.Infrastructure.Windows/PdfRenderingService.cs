using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PDFtoImage;
using UglyToad.PdfPig;

namespace DocConvert.Infrastructure.Windows;

public sealed record RenderedPdfPage(int Index, string ImagePath, double WidthPoints, double HeightPoints);

public static class PdfRenderingService
{
    public static IReadOnlyList<RenderedPdfPage> Render(string inputPath, string outputDirectory, int dpi, CancellationToken token)
    {
        using var document = PdfDocument.Open(inputPath);
        return RenderPages(inputPath, outputDirectory, dpi, Enumerable.Range(0, document.NumberOfPages), token);
    }

    public static IReadOnlyList<RenderedPdfPage> RenderPages(
        string inputPath,
        string outputDirectory,
        int dpi,
        IEnumerable<int> pageIndexes,
        CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        using var document = PdfDocument.Open(inputPath);
        using var pdfStream = File.OpenRead(inputPath);
        var indexes = pageIndexes.Distinct().OrderBy(index => index).ToArray();
        if (indexes.Any(index => index < 0 || index >= document.NumberOfPages))
            throw new ArgumentOutOfRangeException(nameof(pageIndexes), "PDF 页码超出有效范围。");
        var pages = new List<RenderedPdfPage>(indexes.Length);
        var options = new RenderOptions { Dpi = Math.Clamp(dpi, 24, 600) };

        foreach (var index in indexes)
        {
            token.ThrowIfCancellationRequested();
            var page = document.GetPage(index + 1);
            var imagePath = Path.Combine(outputDirectory, $"page-{index + 1:D4}.png");
            pdfStream.Position = 0;
            Conversion.SavePng(imageFilename: imagePath, pdfStream: pdfStream, page: index, leaveOpen: true, options: options);
            pages.Add(new RenderedPdfPage(index, imagePath, page.Width, page.Height));
        }

        return pages;
    }
}
