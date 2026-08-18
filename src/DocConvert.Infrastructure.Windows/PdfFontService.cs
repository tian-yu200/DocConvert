using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using PdfSharp.Fonts;

namespace DocConvert.Infrastructure.Windows;

internal static class PdfFontService
{
    private static readonly object Gate = new();

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void InitializeModule() => EnsureInitialized();
#pragma warning restore CA2255

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
    private const string ChineseBold = "docconvert-chinese-bold";
    private static readonly ConcurrentDictionary<string, byte[]> FontData = new(StringComparer.Ordinal);

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var chinese = familyName.Contains("YaHei", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("黑体", StringComparison.OrdinalIgnoreCase)
            || familyName.Contains("SimHei", StringComparison.OrdinalIgnoreCase);
        if (chinese) return new FontResolverInfo(isBold ? ChineseBold : Chinese, false, isItalic);
        return new FontResolverInfo(isBold ? ArialBold : Arial, false, isItalic);
    }

    public byte[] GetFont(string faceName) => FontData.GetOrAdd(faceName, static name =>
    {
        var fileName = name switch
        {
            Chinese => "Deng.ttf",
            ChineseBold => "Dengb.ttf",
            ArialBold => "arialbd.ttf",
            _ => "arial.ttf"
        };
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到系统字体 {fileName}。", path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 || (bytes[0] != 0 && bytes[0] != (byte)'O'))
            throw new InvalidDataException($"系统字体 {fileName} 不是受支持的 TrueType/OpenType 文件。");
        return bytes;
    });
}
