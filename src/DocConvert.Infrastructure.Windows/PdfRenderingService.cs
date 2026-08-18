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
        Directory.CreateDirectory(outputDirectory);
        using var document = PdfDocument.Open(inputPath);
        using var pdfStream = File.OpenRead(inputPath);
        var pages = new List<RenderedPdfPage>(document.NumberOfPages);
        var options = new RenderOptions { Dpi = Math.Clamp(dpi, 72, 600) };

        for (var index = 0; index < document.NumberOfPages; index++)
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
