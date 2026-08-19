using CSharpMath.Rendering.FrontEnd;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using System.IO;

namespace DocConvert.Infrastructure.Windows;

public static class LatexFormulaService
{
    public static string? Validate(string latex)
    {
        var painter = CreatePainter(latex, 20);
        return painter.ErrorMessage;
    }

    public static byte[] RenderPng(string latex, double fontSize, int pixelWidth, int pixelHeight)
    {
        pixelWidth = Math.Clamp(pixelWidth, 32, 4096);
        pixelHeight = Math.Clamp(pixelHeight, 24, 4096);
        using var surface = SKSurface.Create(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var painter = CreateFittedPainter(latex, fontSize, pixelWidth, pixelHeight);
        ThrowIfInvalid(painter);
        painter.Draw(surface.Canvas, TextAlignment.Center);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] RenderVectorPdf(string latex, double fontSize, double widthPoints, double heightPoints)
    {
        widthPoints = Math.Max(1, widthPoints);
        heightPoints = Math.Max(1, heightPoints);
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            var canvas = document.BeginPage((float)widthPoints, (float)heightPoints);
            var painter = CreateFittedPainter(latex, fontSize, widthPoints, heightPoints);
            ThrowIfInvalid(painter);
            painter.Draw(canvas, TextAlignment.Center);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }

    private static MathPainter CreateFittedPainter(string latex, double fontSize, double width, double height)
    {
        var painter = CreatePainter(latex, fontSize);
        ThrowIfInvalid(painter);
        var measured = painter.Measure();
        if (measured.Width <= 0 || measured.Height <= 0) return painter;
        var scale = Math.Min(width / (measured.Width + 4), height / (measured.Height + 4));
        if (scale < 1) painter.FontSize = Math.Max(3, painter.FontSize * (float)scale);
        return painter;
    }

    private static MathPainter CreatePainter(string latex, double fontSize)
    {
        var painter = new MathPainter
        {
            FontSize = (float)Math.Clamp(fontSize, 5, 144),
            DisplayErrorInline = false,
            TextColor = SKColors.Black,
            LaTeX = latex ?? string.Empty
        };
        return painter;
    }

    private static void ThrowIfInvalid(MathPainter painter)
    {
        if (!string.IsNullOrWhiteSpace(painter.ErrorMessage))
            throw new InvalidOperationException($"LaTeX 公式无效：{painter.ErrorMessage}");
    }
}
