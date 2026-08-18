using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;
using DocConvert.Core;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;

namespace DocConvert.Infrastructure.Windows;

public sealed class PdfCreationEngine : IConversionEngine
{
    public string Name => "PDF 创建引擎";

    public bool CanHandle(DocumentJobRequest request)
    {
        if (request.Kind != JobKind.Convert || !request.OutputPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        return SupportedFiles.IsImage(request.InputPath) || Path.GetExtension(request.InputPath).Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Convert(request, progress, cancellationToken), cancellationToken);

    private static JobResult Convert(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        PdfFontService.EnsureInitialized();
        using var workspace = new JobWorkspace(request.JobId);
        var temporary = workspace.PathFor("output.pdf");
        using var document = new PdfDocument();
        document.Info.Title = Path.GetFileNameWithoutExtension(request.InputPath);
        document.Info.Creator = "DocConvert";

        if (SupportedFiles.IsImage(request.InputPath))
            AddImage(document, request.InputPath, progress, cancellationToken);
        else
            AddText(document, request.InputPath, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        document.Save(temporary);
        workspace.Commit(temporary, request.OutputPath);
        progress?.Report(new JobProgress(100, "PDF 已生成"));
        return JobResult.Ok(request.OutputPath);
    }

    private static void AddImage(PdfDocument document, string inputPath, IProgress<JobProgress>? progress, CancellationToken token)
    {
        using var stream = File.OpenRead(inputPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var frame = decoder.Frames[index];
            var png = new PngBitmapEncoder();
            png.Frames.Add(frame);
            using var buffer = new MemoryStream();
            png.Save(buffer);
            buffer.Position = 0;
            using var image = XImage.FromStream(buffer);
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(image.PointWidth);
            page.Height = XUnit.FromPoint(image.PointHeight);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            progress?.Report(new JobProgress((index + 1d) / decoder.Frames.Count * 95, $"正在写入图片 {index + 1}/{decoder.Frames.Count}"));
        }
    }

    private static void AddText(PdfDocument document, string inputPath, IProgress<JobProgress>? progress, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = File.ReadAllBytes(inputPath);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes).Replace("\r\n", "\n");
        const double margin = 56.7;
        var linesPerPage = 48;
        var lines = WrapText(text, 52).ToList();
        var font = new XFont("Microsoft YaHei", 10.5, XFontStyleEx.Regular);

        for (var offset = 0; offset < lines.Count; offset += linesPerPage)
        {
            token.ThrowIfCancellationRequested();
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var graphics = XGraphics.FromPdfPage(page);
            var formatter = new XTextFormatter(graphics);
            var content = string.Join(Environment.NewLine, lines.Skip(offset).Take(linesPerPage));
            formatter.DrawString(content, font, XBrushes.Black,
                new XRect(margin, margin, page.Width.Point - margin * 2, page.Height.Point - margin * 2));
            progress?.Report(new JobProgress(Math.Min(95, (offset + linesPerPage) * 95d / Math.Max(1, lines.Count)), "正在排版文本"));
        }
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        foreach (var sourceLine in text.Split('\n'))
        {
            if (sourceLine.Length == 0) { yield return string.Empty; continue; }
            for (var index = 0; index < sourceLine.Length; index += width)
                yield return sourceLine.Substring(index, Math.Min(width, sourceLine.Length - index));
        }
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        try { return new UTF8Encoding(false, true).GetString(bytes) is not null ? Encoding.UTF8 : Encoding.GetEncoding("GB18030"); }
        catch { return Encoding.GetEncoding("GB18030"); }
    }

}
