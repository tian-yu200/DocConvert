using DocConvert.Core;

namespace DocConvert.Tests;

public sealed class ConversionRouteTests
{
    [Theory]
    [InlineData("source.pdf", "DOCX", false, true)]
    [InlineData("source.pdf", ".PPTX", false, true)]
    [InlineData("source.docx", "PDF", false, true)]
    [InlineData("source.pptx", "PDF", false, true)]
    [InlineData("source.pdf", "PDF", false, false)]
    [InlineData("source.pdf", "PDF", true, true)]
    [InlineData("source.docx", "PPTX", false, false)]
    public void IsSupported_UsesTheExpectedConversionMatrix(
        string inputPath,
        string targetFormat,
        bool enableOcr,
        bool expected) =>
        Assert.Equal(expected, ConversionRoute.IsSupported(inputPath, targetFormat, enableOcr));

    [Fact]
    public void SelectSupported_MixedQueueKeepsCompatiblePdfForPptx()
    {
        var queue = new[] { "slides.pdf", "notes.docx", "workbook.xlsx" };

        var runnable = ConversionRoute.SelectSupported(queue, path => path, "PPTX", enableOcr: false);

        Assert.Equal(new[] { "slides.pdf" }, runnable);
    }

    [Fact]
    public void SelectSupported_IncompatibleItemDoesNotBlockOtherOfficeToPdfJobs()
    {
        var queue = new[] { "report.docx", "deck.pptx", "already.pdf" };

        var runnable = ConversionRoute.SelectSupported(queue, path => path, "PDF", enableOcr: false);

        Assert.Equal(new[] { "report.docx", "deck.pptx" }, runnable);
    }
}
