namespace DocConvert.Core;

public static class ConversionRoute
{
    public static IReadOnlyList<T> SelectSupported<T>(
        IEnumerable<T> items,
        Func<T, string> inputPathSelector,
        string targetFormat,
        bool enableOcr) =>
        items.Where(item => IsSupported(inputPathSelector(item), targetFormat, enableOcr)).ToArray();

    public static bool IsSupported(string inputPath, string targetFormat, bool enableOcr)
    {
        var input = Path.GetExtension(inputPath).ToLowerInvariant();
        var output = NormalizeExtension(targetFormat);
        return (input, output) switch
        {
            (".pdf", ".docx") => true,
            (".pdf", ".pptx") => true,
            (".pdf", ".jpg") or (".pdf", ".png") => true,
            (".pdf", ".pdf") => enableOcr,
            (".docx", ".pdf") or (".xlsx", ".pdf") or (".pptx", ".pdf") => true,
            (".jpg", ".pdf") or (".jpeg", ".pdf") or (".png", ".pdf") or (".bmp", ".pdf") or (".tif", ".pdf") or (".tiff", ".pdf") => true,
            (".txt", ".pdf") => true,
            _ => false
        };
    }

    public static string NormalizeExtension(string targetFormat) =>
        "." + targetFormat.Trim().TrimStart('.').ToLowerInvariant();
}
