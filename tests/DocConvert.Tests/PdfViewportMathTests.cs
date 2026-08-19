using DocConvert.Core;

namespace DocConvert.Tests;

public sealed class PdfViewportMathTests
{
    [Fact]
    public void GetPageBounds_FitsPageAndPreservesAspectRatio()
    {
        var bounds = PdfViewportMath.GetPageBounds(1000, 700, 595, 842, 1, 0, 0);

        Assert.True(bounds.Width <= 960.001);
        Assert.True(bounds.Height <= 660.001);
        Assert.Equal(595d / 842d, bounds.Width / bounds.Height, 6);
        Assert.Equal((1000 - bounds.Width) / 2, bounds.X, 6);
        Assert.Equal((700 - bounds.Height) / 2, bounds.Y, 6);
    }

    [Fact]
    public void ZoomAroundPoint_PreservesPagePointUnderCursor()
    {
        var oldBounds = new PdfViewportRect(120, 80, 600, 800);
        const double cursorX = 390;
        const double cursorY = 440;
        const double newWidth = 1200;
        const double newHeight = 1600;

        var pan = PdfViewportMath.ZoomAroundPoint(oldBounds, newWidth, newHeight, 1000, 900, cursorX, cursorY);
        var newBounds = new PdfViewportRect(
            (1000 - newWidth) / 2 + pan.PanX,
            (900 - newHeight) / 2 + pan.PanY,
            newWidth,
            newHeight);

        Assert.Equal((cursorX - oldBounds.X) / oldBounds.Width, (cursorX - newBounds.X) / newBounds.Width, 6);
        Assert.Equal((cursorY - oldBounds.Y) / oldBounds.Height, (cursorY - newBounds.Y) / newBounds.Height, 6);
    }

    [Fact]
    public void ClampPan_KeepsMinimumPageAreaVisible()
    {
        var pan = PdfViewportMath.ClampPan(900, 650, 1500, 2100, 100000, -100000, 48);
        var bounds = new PdfViewportRect(
            (900 - 1500) / 2 + pan.PanX,
            (650 - 2100) / 2 + pan.PanY,
            1500,
            2100);

        Assert.True(bounds.X <= 900 - 48 + 0.001);
        Assert.True(bounds.Right >= 48 - 0.001);
        Assert.True(bounds.Y <= 650 - 48 + 0.001);
        Assert.True(bounds.Bottom >= 48 - 0.001);
    }

    [Theory]
    [InlineData(1.0, 144)]
    [InlineData(2.0, 288)]
    [InlineData(3.0, 432)]
    [InlineData(6.0, 600)]
    public void SelectRenderDpi_UsesZoomTiers(double zoom, int expected)
    {
        Assert.Equal(expected, PdfViewportMath.SelectRenderDpi(zoom, 200, 200));
    }

    [Fact]
    public void SelectRenderDpi_RespectsPixelCap()
    {
        const double widthPoints = 10000;
        const double heightPoints = 10000;

        var dpi = PdfViewportMath.SelectRenderDpi(8, widthPoints, heightPoints);
        var pixels = widthPoints / 72d * dpi * heightPoints / 72d * dpi;

        Assert.InRange(dpi, 24, 600);
        Assert.True(pixels <= PdfViewportMath.MaximumRenderPixels);
    }

    [Fact]
    public void GetSelectionBounds_NormalizesReverseDragAndClampsToPage()
    {
        var bounds = PdfViewportMath.GetSelectionBounds(0.8, 1.2, -0.1, 0.25);

        Assert.Equal(0, bounds.X, 6);
        Assert.Equal(0.25, bounds.Y, 6);
        Assert.Equal(0.8, bounds.Width, 6);
        Assert.Equal(0.75, bounds.Height, 6);
    }
}
