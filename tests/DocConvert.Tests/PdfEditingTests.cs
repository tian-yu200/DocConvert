using DocConvert.Core;
using DocConvert.Infrastructure.Windows;
using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace DocConvert.Tests;

public sealed class PdfEditingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocConvert.PdfEditingTests", Guid.NewGuid().ToString("N"));

    public PdfEditingTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExtractTextBlocks_ReturnsNormalizedWordBounds()
    {
        var input = CreateTwoPagePdf();

        var blocks = PdfEditingService.ExtractTextBlocks(input);

        var block = Assert.Single(blocks.Where(item => item.Text == "ORIGINAL"));
        Assert.Equal(0, block.PageIndex);
        Assert.InRange(block.X, 0, 1);
        Assert.InRange(block.Y, 0, 1);
        Assert.InRange(block.Width, 0.01, 0.5);
        Assert.InRange(block.Height, 0.01, 0.2);
        Assert.True(block.X + block.Width <= 1.001);
        Assert.True(block.Y + block.Height <= 1.001);
    }

    [Fact]
    public void OverlaySave_PreservesPagesAndAddsTextAndImage()
    {
        var input = CreateTwoPagePdf();
        var image = CreateOverlayImage();
        var output = Path.Combine(_root, "overlay.pdf");
        var edits = new[]
        {
            new PdfEditElement
            {
                Id = Guid.NewGuid(), Kind = PdfEditKind.TextReplacement, PageIndex = 0,
                X = 0.08, Y = 0.08, Width = 0.42, Height = 0.12,
                Text = "REPLACED", FontFamily = "Arial", FontSize = 20
            },
            new PdfEditElement
            {
                Id = Guid.NewGuid(), Kind = PdfEditKind.Image, PageIndex = 1,
                X = 0.62, Y = 0.62, Width = 0.22, Height = 0.16, ImagePath = image
            }
        };

        PdfEditingService.Save(input, output, edits, PdfEditSaveMode.Overlay, null, CancellationToken.None);

        Assert.True(File.Exists(output));
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, document.PageCount);
            Assert.Equal(360, document.Pages[0].Width.Point, 1);
            Assert.Equal(540, document.Pages[0].Height.Point, 1);
            Assert.Equal(540, document.Pages[1].Width.Point, 1);
            Assert.Equal(360, document.Pages[1].Height.Point, 1);
        }
        using var textDocument = PdfPigDocument.Open(output);
        Assert.Contains("ORIGINAL", textDocument.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REPLACED", textDocument.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
        var rendered = PdfRenderingService.Render(output, Path.Combine(_root, "overlay-render"), 120, CancellationToken.None);
        Assert.Equal(2, rendered.Count);
        using var renderedImage = Cv2.ImRead(rendered[1].ImagePath, ImreadModes.Color);
        Assert.False(renderedImage.Empty());
    }

    [Fact]
    public void SecureSave_RasterizesOnlyEditedPages()
    {
        var input = CreateTwoPagePdf();
        var output = Path.Combine(_root, "secure.pdf");
        var edits = new[]
        {
            new PdfEditElement
            {
                Id = Guid.NewGuid(), Kind = PdfEditKind.TextReplacement, PageIndex = 0,
                X = 0.08, Y = 0.08, Width = 0.42, Height = 0.12,
                Text = "SECURE", FontFamily = "Arial", FontSize = 20
            }
        };

        PdfEditingService.Save(input, output, edits, PdfEditSaveMode.SecureRasterized, null, CancellationToken.None);

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(2, document.PageCount);
            Assert.Equal(360, document.Pages[0].Width.Point, 1);
            Assert.Equal(540, document.Pages[0].Height.Point, 1);
            Assert.Equal(540, document.Pages[1].Width.Point, 1);
            Assert.Equal(360, document.Pages[1].Height.Point, 1);
        }
        using var textDocument = PdfPigDocument.Open(output);
        Assert.DoesNotContain("ORIGINAL", textDocument.GetPage(1).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNTOUCHED", textDocument.GetPage(2).Text, StringComparison.OrdinalIgnoreCase);
        var rendered = PdfRenderingService.Render(output, Path.Combine(_root, "secure-render"), 120, CancellationToken.None);
        Assert.Equal(2, rendered.Count);
        Assert.All(rendered, page => Assert.True(new FileInfo(page.ImagePath).Length > 0));
    }

    [Fact]
    public void NativeSave_RemovesSelectedOriginalGlyphsAndPreservesOtherContent()
    {
        var input = CreateTwoPagePdf();
        var output = Path.Combine(_root, "native.pdf");
        var block = Assert.Single(PdfEditingService.ExtractTextBlocks(input)
            .Where(item => item.PageIndex == 0 && item.Text == "ORIGINAL"));
        var edit = CreateNativeReplacement(block, "REPLACED");

        PdfEditingService.Save(input, output, [edit], PdfEditSaveMode.NativeContent, null, CancellationToken.None);

        using var document = PdfPigDocument.Open(output);
        var firstPage = document.GetPage(1).Text;
        Assert.DoesNotContain("ORIGINAL", firstPage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TEXT", firstPage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REPLACED", firstPage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNTOUCHED", document.GetPage(2).Text, StringComparison.OrdinalIgnoreCase);

        var rendered = PdfRenderingService.Render(output, Path.Combine(_root, "native-render"), 144, CancellationToken.None);
        Assert.Equal(2, rendered.Count);
        Assert.All(rendered, page => Assert.True(new FileInfo(page.ImagePath).Length > 100));
    }

    [Fact]
    public void NativeSave_RejectsReplacementWithoutSourceSelectionAndLeavesNoOutput()
    {
        var input = CreateTwoPagePdf();
        var output = Path.Combine(_root, "native-invalid.pdf");
        var edit = new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.TextReplacement, PageIndex = 0,
            X = 0.1, Y = 0.1, Width = 0.3, Height = 0.08,
            Text = "REPLACED", OriginalText = "ORIGINAL", FontFamily = "Arial", FontSize = 20
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PdfEditingService.Save(
            input, output, [edit], PdfEditSaveMode.NativeContent, null, CancellationToken.None));

        Assert.Contains("原始选区", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void LatexFormulaService_RendersPreviewAndVectorPdf()
    {
        const string latex = @"\frac{x^2 + 1}{\sqrt{y}}";

        Assert.Null(LatexFormulaService.Validate(latex));
        var png = LatexFormulaService.RenderPng(latex, 24, 480, 160);
        var pdf = LatexFormulaService.RenderVectorPdf(latex, 24, 180, 60);

        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.True(pdf.Length > 100);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void LatexFormulaService_ReportsInvalidLatex()
    {
        Assert.False(string.IsNullOrWhiteSpace(LatexFormulaService.Validate(@"\frac{")));
    }

    [Theory]
    [InlineData(PdfEditSaveMode.Overlay)]
    [InlineData(PdfEditSaveMode.SecureRasterized)]
    public void FormulaSave_ProducesRenderablePdf(PdfEditSaveMode mode)
    {
        var input = CreateTwoPagePdf();
        var output = Path.Combine(_root, $"formula-{mode}.pdf");
        var edit = new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Formula, PageIndex = 0,
            X = 0.2, Y = 0.35, Width = 0.5, Height = 0.16,
            Text = @"E = mc^2", FontSize = 28
        };

        PdfEditingService.Save(input, output, [edit], mode, null, CancellationToken.None);

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            Assert.Equal(2, document.PageCount);
        var rendered = PdfRenderingService.RenderPages(
            output, Path.Combine(_root, $"formula-render-{mode}"), 144, [0], CancellationToken.None);
        Assert.Single(rendered);
        Assert.True(new FileInfo(rendered[0].ImagePath).Length > 100);
    }

    [Fact]
    public void Save_RejectsInputPathAndCanReplaceConfirmedOutput()
    {
        var input = CreateTwoPagePdf();
        var edit = new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Text, PageIndex = 0,
            X = 0.1, Y = 0.3, Width = 0.3, Height = 0.1,
            Text = "NEW", FontFamily = "Arial", FontSize = 14
        };
        Assert.Throws<InvalidOperationException>(() => PdfEditingService.Save(
            input, input, [edit], PdfEditSaveMode.Overlay, null, CancellationToken.None));

        var output = Path.Combine(_root, "existing.pdf");
        File.WriteAllText(output, "old");
        Assert.Throws<IOException>(() => PdfEditingService.Save(
            input, output, [edit], PdfEditSaveMode.Overlay, null, CancellationToken.None));

        PdfEditingService.Save(input, output, [edit], PdfEditSaveMode.Overlay, null, CancellationToken.None, true);
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void Save_RejectsEditOutsideDocumentPageRange()
    {
        var input = CreateTwoPagePdf();
        var output = Path.Combine(_root, "invalid-page.pdf");
        var edit = new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Text, PageIndex = 2,
            X = 0.1, Y = 0.1, Width = 0.3, Height = 0.1,
            Text = "INVALID", FontFamily = "Arial", FontSize = 14
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfEditingService.Save(
            input, output, [edit], PdfEditSaveMode.Overlay, null, CancellationToken.None));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Save_RejectsEncryptedPdfEvenWhenItHasAnEmptyUserPassword()
    {
        var input = CreateTwoPagePdf(encryptWithOwnerPassword: true);
        Assert.Contains("/Encrypt", File.ReadAllText(input), StringComparison.Ordinal);
        var output = Path.Combine(_root, "encrypted-output.pdf");
        var edit = new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Text, PageIndex = 0,
            X = 0.1, Y = 0.1, Width = 0.3, Height = 0.1,
            Text = "REJECTED", FontFamily = "Arial", FontSize = 14
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PdfEditingService.Save(
            input, output, [edit], PdfEditSaveMode.Overlay, null, CancellationToken.None));

        Assert.Contains("应用不会移除密码或权限设置", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    private string CreateTwoPagePdf(bool encryptWithOwnerPassword = false)
    {
        var path = Path.Combine(_root, "source.pdf");
        using var pdf = new PdfDocument();
        var first = pdf.AddPage();
        first.Width = XUnit.FromPoint(360);
        first.Height = XUnit.FromPoint(540);
        using (var graphics = XGraphics.FromPdfPage(first))
        {
            graphics.DrawRectangle(XBrushes.White, 0, 0, first.Width.Point, first.Height.Point);
            graphics.DrawString("ORIGINAL TEXT", new XFont("Arial", 22), XBrushes.Black, new XPoint(34, 58));
            graphics.DrawLine(new XPen(XColors.LightGray, 2), 24, 82, 336, 82);
        }
        var second = pdf.AddPage();
        second.Width = XUnit.FromPoint(540);
        second.Height = XUnit.FromPoint(360);
        using (var graphics = XGraphics.FromPdfPage(second))
        {
            graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(244, 248, 250)), 0, 0, second.Width.Point, second.Height.Point);
            graphics.DrawString("UNTOUCHED PAGE", new XFont("Arial", 20), XBrushes.Black, new XPoint(42, 64));
        }
        if (encryptWithOwnerPassword)
        {
            pdf.SecuritySettings.UserPassword = string.Empty;
            pdf.SecuritySettings.OwnerPassword = "owner-password";
        }
        pdf.Save(path);
        return path;
    }

    private string CreateOverlayImage()
    {
        var path = Path.Combine(_root, "overlay.png");
        using var image = new Mat(80, 120, MatType.CV_8UC3, new Scalar(30, 160, 220));
        Cv2.Rectangle(image, new Rect(8, 8, 104, 64), new Scalar(230, 245, 250), 3);
        Cv2.ImWrite(path, image);
        return path;
    }

    private static PdfEditElement CreateNativeReplacement(PdfTextBlock block, string replacement) => new()
    {
        Id = Guid.NewGuid(), Kind = PdfEditKind.TextReplacement, PageIndex = block.PageIndex,
        X = Math.Max(0, block.X - 0.002), Y = Math.Max(0, block.Y - 0.002),
        Width = Math.Min(1, block.Width + 0.004), Height = Math.Min(1, Math.Max(block.Height + 0.006, 0.025)),
        Text = replacement, OriginalText = block.Text, FontFamily = "Arial", FontSize = block.FontSize,
        SourceX = Math.Max(0, block.X - 0.002), SourceY = Math.Max(0, block.Y - 0.002),
        SourceWidth = Math.Min(1, block.Width + 0.004), SourceHeight = Math.Min(1, block.Height + 0.004)
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { }
    }
}
