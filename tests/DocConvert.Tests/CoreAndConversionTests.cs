using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using DocConvert.Core;
using DocConvert.Infrastructure.Windows;
using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace DocConvert.Tests;

public sealed class CoreAndConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocConvert.Tests", Guid.NewGuid().ToString("N"));

    public CoreAndConversionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OutputPaths_NeverOverwriteAndUseExpectedSuffixes()
    {
        var service = new OutputPathService();
        var input = Path.Combine(_root, "报告.pdf");
        File.WriteAllText(input, "source");
        var first = service.CreateWatermarkOutputPath(input);
        Assert.EndsWith("报告_无水印.pdf", first);
        File.WriteAllText(first, "existing");
        Assert.EndsWith("报告_无水印 (1).pdf", service.CreateWatermarkOutputPath(input));
    }

    [Theory]
    [InlineData("1,3-5", 5, new[] { 0, 2, 3, 4 })]
    [InlineData("5-3", 5, new[] { 2, 3, 4 })]
    [InlineData("1，2；4", 4, new[] { 0, 1, 3 })]
    public void PageRangeParser_ParsesHumanPageNumbers(string value, int count, int[] expected) =>
        Assert.Equal(expected, PageRangeParser.Parse(value, count));

    [Fact]
    public void PageRangeParser_RejectsOutOfRangePages() =>
        Assert.Throws<FormatException>(() => PageRangeParser.Parse("0,4", 3));

    [Fact]
    public void PageRangeParser_CurrentPageUsesOnlyRegionsDrawnOnThatPage()
    {
        var regions = new[]
        {
            new WatermarkRegion(0, 0.1, 0.1, 0.2, 0.2),
            new WatermarkRegion(1, 0.4, 0.4, 0.2, 0.2)
        };

        var scoped = PageRangeParser.ApplyScope(regions, WatermarkScope.CurrentPage, null, 1, 2);

        var region = Assert.Single(scoped);
        Assert.Equal(1, region.PageIndex);
        Assert.Equal(0.4, region.X);
    }

    [Fact]
    public async Task TxtToPdf_CreatesReadablePdf()
    {
        var input = Path.Combine(_root, "中文 路径.txt");
        var output = Path.Combine(_root, "文本.pdf");
        await File.WriteAllTextAsync(input, "第一行 DocConvert\n第二行中文内容");
        var result = await new PdfCreationEngine().ExecuteAsync(Request(input, output), null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public async Task ImageToPdf_CreatesOnePage(string extension)
    {
        var input = Path.Combine(_root, "image" + extension);
        var output = Path.Combine(_root, $"image-{extension.TrimStart('.')}.pdf");
        using (var image = new Mat(120, 180, MatType.CV_8UC3, new Scalar(245, 245, 245)))
        {
            Cv2.PutText(image, "TEST", new Point(35, 70), HersheyFonts.HersheySimplex, 1.4, new Scalar(20, 20, 20), 2);
            Cv2.ImWrite(input, image);
        }
        var result = await new PdfCreationEngine().ExecuteAsync(Request(input, output), null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Single(document.Pages);
    }

    [Fact]
    public async Task PdfToPng_CreatesOneImagePerPageAtRequestedDpi()
    {
        var input = Path.Combine(_root, "two-pages.pdf");
        var output = Path.Combine(_root, "two-pages.png");
        using (var pdf = new PdfDocument())
        {
            for (var index = 0; index < 2; index++)
            {
                var page = pdf.AddPage();
                page.Width = XUnit.FromPoint(72);
                page.Height = XUnit.FromPoint(144);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawString($"Page {index + 1}", new XFont("Arial", 12), XBrushes.Black, new XPoint(8, 30));
            }
            pdf.Save(input);
        }

        var request = Request(input, output) with { Conversion = new ConversionOptions { RenderDpi = 150 } };
        var result = await new PdfToImageEngine().ExecuteAsync(request, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(output, result.OutputPath);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(_root, "two-pages_002.png")));
        using var image = Cv2.ImRead(output, ImreadModes.Unchanged);
        Assert.InRange(image.Width, 149, 151);
        Assert.InRange(image.Height, 299, 301);
    }

    [Fact]
    public async Task PdfToJpg_AvoidsCollisionsAcrossTheWholePageSet()
    {
        var input = Path.Combine(_root, "collision.pdf");
        var output = Path.Combine(_root, "collision.jpg");
        var existingSecondPage = Path.Combine(_root, "collision_002.jpg");
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            pdf.AddPage();
            pdf.Save(input);
        }
        await File.WriteAllTextAsync(existingSecondPage, "keep");

        var result = await new PdfToImageEngine().ExecuteAsync(Request(input, output), null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.EndsWith("collision (1).jpg", result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.True(File.Exists(Path.Combine(_root, "collision (1)_002.jpg")));
        Assert.Equal("keep", await File.ReadAllTextAsync(existingSecondPage));
        using var image = Cv2.ImRead(result.OutputPath, ImreadModes.Color);
        Assert.False(image.Empty());
    }

    [Fact]
    public async Task Ocr_CreatesSearchablePdfFromImageOnlyPage()
    {
        var imagePath = Path.Combine(_root, "ocr-source.png");
        var scannedPdf = Path.Combine(_root, "scanned.pdf");
        var searchablePdf = Path.Combine(_root, "searchable.pdf");
        using (var image = new Mat(500, 1500, MatType.CV_8UC3, Scalar.White))
        {
            Cv2.PutText(image, "DOC CONVERT 2026", new Point(90, 290), HersheyFonts.HersheySimplex, 3.2, Scalar.Black, 7, LineTypes.AntiAlias);
            Cv2.ImWrite(imagePath, image);
        }

        var imageResult = await new PdfCreationEngine().ExecuteAsync(Request(imagePath, scannedPdf), null, CancellationToken.None);
        Assert.True(imageResult.Success, imageResult.Error);
        var request = Request(scannedPdf, searchablePdf) with
        {
            Conversion = new ConversionOptions { EnableOcr = true, OcrLanguages = "eng" }
        };
        var result = await new OcrConversionEngine().ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var document = PdfPigDocument.Open(searchablePdf);
        var text = string.Join(" ", document.GetPages().Select(page => page.Text));
        Assert.Contains("CONVERT", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(300)]
    public async Task PdfToPptx_UsesOneImagePerSlideWithoutTextOverlays(int dpi)
    {
        var input = Path.Combine(_root, $"mixed-{dpi}.pdf");
        var output = Path.Combine(_root, $"mixed-{dpi}.pptx");
        using (var pdf = new PdfDocument())
        {
            var portrait = pdf.AddPage();
            portrait.Size = PdfSharp.PageSize.A4;
            using (var graphics = XGraphics.FromPdfPage(portrait))
            {
                graphics.DrawString("Presentation title", new XFont("Arial", 24), XBrushes.Black, new XPoint(70, 90));
                graphics.DrawString("First paragraph of body text", new XFont("Arial", 14), XBrushes.Black, new XPoint(70, 135));
                graphics.DrawString("Second paragraph stays in the page image", new XFont("Arial", 14), XBrushes.Black, new XPoint(70, 165));
            }
            var landscape = pdf.AddPage();
            landscape.Width = XUnit.FromPoint(842);
            landscape.Height = XUnit.FromPoint(420);
            using (var graphics = XGraphics.FromPdfPage(landscape))
                graphics.DrawString("Wide page with rotated and complex layout", new XFont("Arial", 20), XBrushes.Black, new XPoint(90, 110));
            pdf.Save(input);
        }

        var request = Request(input, output) with { Conversion = new ConversionOptions { RenderDpi = dpi } };
        var result = await new PdfToPptxEngine().ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var presentation = PresentationDocument.Open(output, false);
        var errors = new OpenXmlValidator().Validate(presentation).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine,
            errors.Select(error => $"{error.Part?.Uri} {error.Path?.XPath}: {error.Description}")));
        var presentationPart = Assert.IsType<PresentationPart>(presentation.PresentationPart);
        var root = Assert.IsType<P.Presentation>(presentationPart.Presentation);
        Assert.Equal(12192000, root.SlideSize!.Cx!.Value);
        Assert.Equal(6858000, root.SlideSize.Cy!.Value);
        Assert.Equal(P.SlideSizeValues.Screen16x9, root.SlideSize.Type!.Value);

        var slides = presentationPart.SlideParts.ToArray();
        Assert.Equal(2, slides.Length);
        var expectedRatios = new[] { 595d / 842d, 842d / 420d };
        for (var index = 0; index < slides.Length; index++)
        {
            var slidePart = slides[index];
            var imagePart = Assert.Single(slidePart.ImageParts);
            Assert.Equal("image/png", imagePart.ContentType);
            var slide = Assert.IsType<P.Slide>(slidePart.Slide);
            var picture = Assert.Single(slide.Descendants<P.Picture>());
            Assert.Empty(slide.Descendants<P.Shape>().Where(shape => shape.TextBody is not null));
            Assert.Empty(slide.Descendants<A.Text>());
            var shapeProperties = Assert.IsType<P.ShapeProperties>(picture.ShapeProperties);
            var transform = Assert.IsType<A.Transform2D>(shapeProperties.Transform2D);
            var offset = transform.Offset!;
            var extents = transform.Extents!;
            Assert.True(offset.X!.Value >= 0 && offset.Y!.Value >= 0);
            Assert.True(offset.X.Value + extents.Cx!.Value <= 12192000);
            Assert.True(offset.Y.Value + extents.Cy!.Value <= 6858000);
            var actualRatio = extents.Cx.Value / (double)extents.Cy.Value;
            Assert.InRange(actualRatio, expectedRatios[index] - 0.01, expectedRatios[index] + 0.01);
        }
    }

    [Fact]
    public async Task ImageWatermarkRemoval_ChangesOnlyNearMask()
    {
        var unicodeRoot = Path.Combine(_root, "中文目录");
        Directory.CreateDirectory(unicodeRoot);
        var input = Path.Combine(unicodeRoot, "带水印.png");
        var output = Path.Combine(unicodeRoot, "带水印_无水印.png");
        using (var image = new Mat(200, 300, MatType.CV_8UC3, new Scalar(255, 255, 255)))
        {
            Cv2.Rectangle(image, new Rect(20, 20, 40, 40), new Scalar(30, 120, 220), -1);
            Cv2.Rectangle(image, new Rect(100, 85, 130, 35), new Scalar(120, 120, 120), -1);
            Assert.True(Cv2.ImEncode(".png", image, out var encoded));
            File.WriteAllBytes(input, encoded);
        }

        var request = Request(input, output) with
        {
            Kind = JobKind.RemoveWatermark,
            Watermark = new WatermarkOptions { Regions = [new WatermarkRegion(0, 0.27, 0.38, 0.58, 0.25)] }
        };
        var result = await new ImageWatermarkRemovalEngine().ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        using var before = Cv2.ImDecode(File.ReadAllBytes(input), ImreadModes.Unchanged);
        using var after = Cv2.ImDecode(File.ReadAllBytes(output), ImreadModes.Unchanged);
        Assert.Equal(before.At<Vec3b>(30, 30), after.At<Vec3b>(30, 30));
        using var difference = new Mat();
        Cv2.Absdiff(before, after, difference);
        var changed = Cv2.CountNonZero(difference.Reshape(1));
        Assert.True(changed > 0, $"Expected inpainting to change pixels, changed={changed}.");
    }

    [Fact]
    public async Task ImageWatermarkRemoval_PreservesTextUnderCentralWatermark()
    {
        var input = Path.Combine(_root, "central-watermark.png");
        var output = Path.Combine(_root, "central-watermark-cleaned.png");
        using var clean = new Mat(220, 420, MatType.CV_8UC3, Scalar.White);
        Cv2.PutText(clean, "IMPORTANT TEXT", new Point(28, 132), HersheyFonts.HersheySimplex,
            1.15, new Scalar(18, 18, 18), 3, LineTypes.AntiAlias);
        using var watermarked = clean.Clone();
        using (var overlay = clean.Clone())
        {
            Cv2.PutText(overlay, "DRAFT", new Point(92, 150), HersheyFonts.HersheyDuplex,
                2.15, new Scalar(35, 35, 230), 9, LineTypes.AntiAlias);
            Cv2.AddWeighted(overlay, 0.34, watermarked, 0.66, 0, watermarked);
        }
        Cv2.ImWrite(input, watermarked);

        var request = Request(input, output) with
        {
            Kind = JobKind.RemoveWatermark,
            Watermark = new WatermarkOptions { Regions = [new WatermarkRegion(0, 0.17, 0.25, 0.72, 0.55)] }
        };
        var result = await new ImageWatermarkRemovalEngine().ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(result.Success, result.Error);

        using var after = Cv2.ImRead(output, ImreadModes.Color);
        var watermarkPixels = new List<(int Y, int X)>();
        var coveredTextPixels = new List<(int Y, int X)>();
        for (var y = 55; y < 176; y++)
        for (var x = 71; x < 374; x++)
        {
            var expected = clean.At<Vec3b>(y, x);
            var before = watermarked.At<Vec3b>(y, x);
            if (MaxChannelDifference(expected, before) < 18) continue;
            if (Luminance(expected) > 245) watermarkPixels.Add((y, x));
            if (Luminance(expected) < 90) coveredTextPixels.Add((y, x));
        }

        Assert.NotEmpty(watermarkPixels);
        Assert.NotEmpty(coveredTextPixels);
        var watermarkErrorBefore = watermarkPixels.Average(point =>
            (double)MaxChannelDifference(clean.At<Vec3b>(point.Y, point.X), watermarked.At<Vec3b>(point.Y, point.X)));
        var watermarkErrorAfter = watermarkPixels.Average(point =>
            (double)MaxChannelDifference(clean.At<Vec3b>(point.Y, point.X), after.At<Vec3b>(point.Y, point.X)));
        Assert.True(watermarkErrorAfter < watermarkErrorBefore * 0.55,
            $"Expected watermark to fade without masking the full rectangle: before={watermarkErrorBefore:F1}, after={watermarkErrorAfter:F1}.");

        var retainedDarkText = coveredTextPixels.Count(point => Luminance(after.At<Vec3b>(point.Y, point.X)) < 120);
        Assert.True(retainedDarkText >= coveredTextPixels.Count * 0.9,
            $"Expected covered text strokes to stay sharp: retained={retainedDarkText}/{coveredTextPixels.Count}.");
    }

    [Fact]
    public void Safety_RejectsReadOnlyAndSignedPdfMarkers()
    {
        var readOnly = Path.Combine(_root, "readonly.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Assert.Throws<InvalidOperationException>(() => DocumentSafety.EnsureModifiable(readOnly));
        File.SetAttributes(readOnly, FileAttributes.Normal);

        var signed = Path.Combine(_root, "signed.pdf");
        File.WriteAllText(signed, "%PDF-1.4\n/Sig /ByteRange\n%%EOF");
        Assert.Throws<InvalidOperationException>(() => DocumentSafety.EnsureModifiable(signed));
    }

    [Fact]
    public async Task JobRunner_ReportsUnsupportedCombination()
    {
        var request = Request(Path.Combine(_root, "input.bmp"), Path.Combine(_root, "output.docx"));
        var result = await new DocumentJobRunner([]).RunAsync(request, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("没有可处理", result.Error);
    }

    private static DocumentJobRequest Request(string input, string output) => new()
    {
        JobId = Guid.NewGuid(),
        Kind = JobKind.Convert,
        InputPath = input,
        OutputPath = output,
        TargetExtension = Path.GetExtension(output)
    };

    private static int MaxChannelDifference(Vec3b left, Vec3b right) =>
        Math.Max(Math.Abs(left.Item0 - right.Item0),
            Math.Max(Math.Abs(left.Item1 - right.Item1), Math.Abs(left.Item2 - right.Item2)));

    private static double Luminance(Vec3b pixel) =>
        pixel.Item0 * 0.114 + pixel.Item1 * 0.587 + pixel.Item2 * 0.299;

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }
}
