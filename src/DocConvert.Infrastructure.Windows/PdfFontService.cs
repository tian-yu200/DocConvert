using System;
using System.IO;
using PdfSharp.Fonts;

namespace DocConvert.Infrastructure.Windows;

internal static class PdfFontService
{
    private static readonly object Gate = new();

    internal static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new WindowsDocumentFontResolver();
        }
    }
}

internal sealed class WindowsDocumentFontResolver : IFontResolver
{
    private const string Arial = "docconvert-arial";
    private const string ArialBold = "docconvert-arial-bold";
    private const string Chinese = "docconvert-chinese";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var chinese = familyName.Contains("YaHei", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("黑体", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("SimHei", StringComparison.OrdinalIgnoreCase);
        if (chinese) return new FontResolverInfo(Chinese, isBold, isItalic);
        return new FontResolverInfo(isBold ? ArialBold : Arial, false, isItalic);
    }

    public byte[] GetFont(string faceName)
    {
        var fileName = faceName switch
        {
            Chinese => "simhei.ttf",
            ArialBold => "arialbd.ttf",
            _ => "arial.ttf"
        };
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到系统字体 {fileName}。", path);
        return File.ReadAllBytes(path);
    }
}
