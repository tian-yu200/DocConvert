namespace DocConvert.Core;

public readonly record struct PdfViewportRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public readonly record struct PdfNormalizedRect(double X, double Y, double Width, double Height);

public static class PdfViewportMath
{
    public const double MinimumZoom = 0.25;
    public const double MaximumZoom = 8;
    public const long MaximumRenderPixels = 40_000_000;

    public static PdfViewportRect GetPageBounds(
        double viewportWidth,
        double viewportHeight,
        double pageWidth,
        double pageHeight,
        double zoom,
        double panX,
        double panY,
        double margin = 20)
    {
        viewportWidth = Math.Max(1, viewportWidth);
        viewportHeight = Math.Max(1, viewportHeight);
        pageWidth = Math.Max(1, pageWidth);
        pageHeight = Math.Max(1, pageHeight);
        zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        var availableWidth = Math.Max(1, viewportWidth - margin * 2);
        var availableHeight = Math.Max(1, viewportHeight - margin * 2);
        var fitScale = Math.Min(availableWidth / pageWidth, availableHeight / pageHeight);
        var width = pageWidth * fitScale * zoom;
        var height = pageHeight * fitScale * zoom;
        return new PdfViewportRect(
            (viewportWidth - width) / 2 + panX,
            (viewportHeight - height) / 2 + panY,
            width,
            height);
    }

    public static (double PanX, double PanY) ZoomAroundPoint(
        PdfViewportRect oldBounds,
        double newWidth,
        double newHeight,
        double viewportWidth,
        double viewportHeight,
        double cursorX,
        double cursorY)
    {
        var normalizedX = oldBounds.Width <= 0 ? 0.5 : (cursorX - oldBounds.X) / oldBounds.Width;
        var normalizedY = oldBounds.Height <= 0 ? 0.5 : (cursorY - oldBounds.Y) / oldBounds.Height;
        var centeredX = (viewportWidth - newWidth) / 2;
        var centeredY = (viewportHeight - newHeight) / 2;
        return (
            cursorX - normalizedX * newWidth - centeredX,
            cursorY - normalizedY * newHeight - centeredY);
    }

    public static (double PanX, double PanY) ClampPan(
        double viewportWidth,
        double viewportHeight,
        double pageWidth,
        double pageHeight,
        double panX,
        double panY,
        double minimumVisible = 48)
    {
        var centeredX = (viewportWidth - pageWidth) / 2;
        var centeredY = (viewportHeight - pageHeight) / 2;
        var minPanX = minimumVisible - (centeredX + pageWidth);
        var maxPanX = viewportWidth - minimumVisible - centeredX;
        var minPanY = minimumVisible - (centeredY + pageHeight);
        var maxPanY = viewportHeight - minimumVisible - centeredY;
        return (
            Math.Clamp(panX, Math.Min(minPanX, maxPanX), Math.Max(minPanX, maxPanX)),
            Math.Clamp(panY, Math.Min(minPanY, maxPanY), Math.Max(minPanY, maxPanY)));
    }

    public static int SelectRenderDpi(
        double zoom,
        double pageWidthPoints,
        double pageHeightPoints,
        long maximumPixels = MaximumRenderPixels)
    {
        var requested = zoom switch
        {
            <= 1.25 => 144,
            <= 2.5 => 288,
            <= 4 => 432,
            _ => 600
        };
        var pixels = pageWidthPoints / 72d * requested * pageHeightPoints / 72d * requested;
        if (pixels <= maximumPixels) return requested;
        var capped = requested * Math.Sqrt(maximumPixels / pixels);
        return Math.Clamp((int)Math.Floor(capped), 24, requested);
    }

    public static PdfNormalizedRect GetSelectionBounds(double startX, double startY, double endX, double endY)
    {
        startX = Math.Clamp(startX, 0, 1);
        startY = Math.Clamp(startY, 0, 1);
        endX = Math.Clamp(endX, 0, 1);
        endY = Math.Clamp(endY, 0, 1);
        return new PdfNormalizedRect(
            Math.Min(startX, endX),
            Math.Min(startY, endY),
            Math.Abs(endX - startX),
            Math.Abs(endY - startY));
    }
}
