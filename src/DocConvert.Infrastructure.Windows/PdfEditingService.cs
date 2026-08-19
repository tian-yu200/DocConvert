using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using DocConvert.Core;
using PdfLexer.Content;
using PdfLexer.Content.Model;
using PdfLexer.Fonts;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfLexerDocument = PdfLexer.PdfDocument;

namespace DocConvert.Infrastructure.Windows;

public static class PdfEditingService
{
    public static IReadOnlyList<PdfTextBlock> ExtractTextBlocks(string inputPath)
    {
        DocumentSafety.EnsureModifiable(inputPath);
        using var document = UglyToad.PdfPig.PdfDocument.Open(inputPath);
        var blocks = new List<PdfTextBlock>();
        for (var pageIndex = 0; pageIndex < document.NumberOfPages; pageIndex++)
        {
            var page = document.GetPage(pageIndex + 1);
            foreach (var word in page.GetWords())
            {
                var bounds = word.BoundingBox;
                if (string.IsNullOrWhiteSpace(word.Text) || bounds.Width <= 0 || bounds.Height <= 0) continue;
                blocks.Add(new PdfTextBlock(
                    pageIndex,
                    word.Text,
                    Clamp(bounds.Left / page.Width),
                    Clamp((page.Height - bounds.Top) / page.Height),
                    Clamp(bounds.Width / page.Width),
                    Clamp(bounds.Height / page.Height),
                    Math.Max(6, bounds.Height)));
            }
        }
        return blocks;
    }

    public static void Save(
        string inputPath,
        string outputPath,
        IReadOnlyCollection<PdfEditElement> edits,
        PdfEditSaveMode mode,
        IProgress<JobProgress>? progress,
        CancellationToken token,
        bool overwriteExisting = false)
    {
        DocumentSafety.EnsureModifiable(inputPath);
        if (edits.Count == 0) throw new InvalidOperationException("当前 PDF 没有可保存的编辑内容。");
        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("为保护原文件，请将编辑结果保存为新的 PDF。");

        PdfFontService.EnsureInitialized();
        using var workspace = new JobWorkspace(Guid.NewGuid());
        var temporary = workspace.PathFor("edited.pdf");
        switch (mode)
        {
            case PdfEditSaveMode.SecureRasterized:
                SaveSecure(inputPath, temporary, edits, workspace, progress, token);
                break;
            case PdfEditSaveMode.NativeContent:
                SaveNative(inputPath, temporary, edits, workspace, progress, token);
                break;
            default:
                SaveOverlay(inputPath, temporary, edits, progress, token, true);
                break;
        }
        Commit(workspace, temporary, outputPath, overwriteExisting);
    }

    private static void Commit(JobWorkspace workspace, string temporaryPath, string outputPath, bool overwriteExisting)
    {
        if (!overwriteExisting || !File.Exists(outputPath))
        {
            workspace.Commit(temporaryPath, outputPath);
            return;
        }

        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(temporaryPath, staging, true);
            File.Move(staging, outputPath, true);
        }
        finally
        {
            try { if (File.Exists(staging)) File.Delete(staging); }
            catch { }
        }
    }

    private static void SaveOverlay(
        string inputPath,
        string outputPath,
        IReadOnlyCollection<PdfEditElement> edits,
        IProgress<JobProgress>? progress,
        CancellationToken token,
        bool coverOriginalText)
    {
        using var source = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
        using var output = new PdfSharp.Pdf.PdfDocument();
        EnsureValidPageIndexes(edits, source.PageCount);
        var byPage = edits.GroupBy(edit => edit.PageIndex).ToDictionary(group => group.Key, group => group.ToArray());
        for (var pageIndex = 0; pageIndex < source.PageCount; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            var page = output.AddPage(source.Pages[pageIndex]);
            if (byPage.TryGetValue(pageIndex, out var pageEdits))
            {
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                DrawEdits(graphics, page.Width.Point, page.Height.Point, pageEdits, coverOriginalText);
            }
            progress?.Report(new JobProgress((pageIndex + 1d) / source.PageCount * 95,
                $"正在写入 PDF 页面 {pageIndex + 1}/{source.PageCount}"));
        }
        output.Save(outputPath);
    }

    private static void SaveNative(
        string inputPath,
        string outputPath,
        IReadOnlyCollection<PdfEditElement> edits,
        JobWorkspace workspace,
        IProgress<JobProgress>? progress,
        CancellationToken token)
    {
        var replacements = edits.Where(edit => edit.Kind == PdfEditKind.TextReplacement).ToArray();
        if (replacements.Length == 0)
            throw new InvalidOperationException("原生保存至少需要一个文字替换对象；其他对象请使用普通保存。");

        var nativeBase = workspace.PathFor("native-content.pdf");
        RemoveOriginalText(inputPath, nativeBase, replacements, progress, token);
        progress?.Report(new JobProgress(55, "原文字已从内容流删除，正在写入新内容"));
        SaveOverlay(nativeBase, outputPath, edits, progress, token, false);
    }

    private static void RemoveOriginalText(
        string inputPath,
        string outputPath,
        IReadOnlyList<PdfEditElement> replacements,
        IProgress<JobProgress>? progress,
        CancellationToken token)
    {
        using var document = PdfLexerDocument.Open(inputPath);
        if (document.Catalog.ContainsKey(PdfLexer.PdfName.Names)
            || document.Catalog.ContainsKey(new PdfLexer.PdfName("StructTreeRoot")))
            throw new InvalidOperationException("此 PDF 含名称树、附件或标签结构，当前版本为避免破坏文档结构已拒绝原生保存。请使用普通保存或安全保存。");

        var byPage = replacements.GroupBy(edit => edit.PageIndex).OrderBy(group => group.Key).ToArray();
        foreach (var group in byPage)
        {
            token.ThrowIfCancellationRequested();
            var page = document.Pages[group.Key];
            if ((int)(page.Rotate ?? 0) % 360 != 0)
                throw new InvalidOperationException($"第 {group.Key + 1} 页使用了页面旋转，当前版本暂不支持原生文字删除。请使用普通保存或安全保存。");

            var originalNodes = page.GetContentNodes<double>();
            if (originalNodes.Flatten().Any(node => node is FormContent<double>))
                throw new InvalidOperationException($"第 {group.Key + 1} 页的文字位于或混用了 Form XObject，当前版本暂不原生改写此类页面。");

            foreach (var edit in group)
            {
                token.ThrowIfCancellationRequested();
                ValidateNativeReplacement(edit);
                var target = ToPdfLexerRect(page, edit);
                var candidates = page.GetContentNodes<double>()
                    .Flatten()
                    .OfType<TextContent<double>>()
                    .Where(text => SelectionTextEquals(text.CopyArea(target)?.Text, edit.OriginalText))
                    .ToArray();
                if (candidates.Length != 1)
                    throw new InvalidOperationException(candidates.Length == 0
                        ? $"第 {group.Key + 1} 页无法把“{edit.OriginalText}”唯一映射到原始字形，已停止原生保存。"
                        : $"第 {group.Key + 1} 页有多个内容节点匹配“{edit.OriginalText}”，为防止误删已停止原生保存。");

                var removed = 0;
                var mutation = new CachedContentMutation((IContentGroup<double> node) =>
                {
                    if (removed > 0
                        || node is not TextContent<double> text
                        || !SelectionTextEquals(text.CopyArea(target)?.Text, edit.OriginalText))
                        return node;
                    var replacement = RemoveSelectedGlyphs(text, target, edit.OriginalText);
                    removed++;
                    return replacement!;
                });
                page = mutation.Apply(page);
                if (removed != 1)
                    throw new InvalidOperationException($"第 {group.Key + 1} 页的原文字删除结果不唯一，已停止保存。");
            }
            document.Pages[group.Key] = page;
            progress?.Report(new JobProgress(10 + 40d * (group.Key + 1) / document.Pages.Count,
                $"正在原生改写 PDF 页面 {group.Key + 1}/{document.Pages.Count}"));
        }
        document.SaveTo(outputPath);
    }

    private static TextContent<double>? RemoveSelectedGlyphs(
        TextContent<double> text,
        PdfRect<double> target,
        string expectedText)
    {
        var bounds = text.GetGlyphBoundingBoxes().GetEnumerator();
        var selectedText = new StringBuilder();
        var segments = new List<TextSegment<double>>(text.Segments.Count);
        var remainingGlyphs = 0;

        foreach (var segment in text.Segments)
        {
            EnsureHorizontalText(segment.GraphicsState);
            var rebuilt = new List<GlyphOrShift<double>>(segment.Glyphs.Count);
            var skippedAdvance = 0d;
            var segmentChanged = false;
            foreach (var glyphOrShift in segment.Glyphs)
            {
                if (glyphOrShift.Glyph is null)
                {
                    skippedAdvance -= glyphOrShift.Shift;
                    continue;
                }

                if (!bounds.MoveNext())
                    throw new InvalidOperationException("PDF 文字字形与其边界数量不一致，已停止原生保存。");

                if (target.CheckEnclosure(bounds.Current) != EncloseType.None)
                {
                    AppendGlyphText(selectedText, glyphOrShift.Glyph);
                    skippedAdvance += GetGlyphAdvance(segment.GraphicsState, glyphOrShift);
                    segmentChanged = true;
                    continue;
                }

                FlushSkippedAdvance(rebuilt, ref skippedAdvance);
                rebuilt.Add(glyphOrShift);
                remainingGlyphs++;
            }

            FlushSkippedAdvance(rebuilt, ref skippedAdvance);
            segments.Add(segmentChanged ? segment with { Glyphs = rebuilt } : segment);
        }

        if (bounds.MoveNext())
            throw new InvalidOperationException("PDF 文字字形与其边界数量不一致，已停止原生保存。");
        if (!SelectionTextEquals(selectedText.ToString(), expectedText))
            throw new InvalidOperationException($"选区实际命中的文字为“{selectedText}”，与“{expectedText}”不一致，已停止原生保存。");

        return remainingGlyphs == 0
            ? null
            : new TextContent<double> { LineMatrix = text.LineMatrix, Segments = segments };
    }

    private static double GetGlyphAdvance(GfxState<double> state, GlyphOrShift<double> glyphOrShift)
    {
        var glyph = glyphOrShift.Glyph!;
        if (state.FontSize == 0)
            throw new InvalidOperationException("PDF 文字字号为零，无法安全计算原生删除后的字形位移。");

        var spacing = state.CharSpacing + (glyph.IsWordSpace ? state.WordSpacing : 0);
        return 1000d * glyph.w0 + 1000d * spacing / state.FontSize;
    }

    private static void EnsureHorizontalText(GfxState<double> state)
    {
        var matrix = state.TRM;
        var scale = Math.Max(1, Math.Max(Math.Abs(matrix.A), Math.Abs(matrix.D)));
        if (Math.Abs(matrix.B) > scale * 0.000001 || Math.Abs(matrix.C) > scale * 0.000001)
            throw new InvalidOperationException("当前版本仅支持水平、未倾斜文字的原生删除。请使用普通保存或安全保存。");
    }

    private static void FlushSkippedAdvance(List<GlyphOrShift<double>> glyphs, ref double skippedAdvance)
    {
        if (Math.Abs(skippedAdvance) < 0.000001) return;
        glyphs.Add(new GlyphOrShift<double>(-skippedAdvance));
        skippedAdvance = 0;
    }

    private static void AppendGlyphText(StringBuilder text, Glyph glyph)
    {
        if (glyph.MultiChar is not null)
            text.Append(glyph.MultiChar);
        else
            text.Append(glyph.Char);
    }

    private static PdfRect<double> ToPdfLexerRect(PdfLexer.DOM.PdfPage page, PdfEditElement edit)
    {
        var media = page.MediaBox;
        var pageWidth = (double)media.Width;
        var pageHeight = (double)media.Height;
        var left = (double)media.LLx + Clamp(edit.SourceX!.Value) * pageWidth;
        var right = left + Clamp(edit.SourceWidth!.Value) * pageWidth;
        var top = (double)media.URy - Clamp(edit.SourceY!.Value) * pageHeight;
        var bottom = top - Clamp(edit.SourceHeight!.Value) * pageHeight;
        return new PdfRect<double> { LLx = left, LLy = bottom, URx = right, URy = top };
    }

    private static void ValidateNativeReplacement(PdfEditElement edit)
    {
        if (string.IsNullOrWhiteSpace(edit.OriginalText)
            || edit.SourceX is null || edit.SourceY is null || edit.SourceWidth is null || edit.SourceHeight is null
            || edit.SourceWidth <= 0 || edit.SourceHeight <= 0)
            throw new InvalidOperationException("文字替换对象缺少原始文字或原始选区，无法进行原生保存。请重新选择要替换的文字。");
    }

    private static bool SelectionTextEquals(string? actual, string expected) =>
        string.Equals(actual?.Trim(), expected.Trim(), StringComparison.Ordinal);

    private static void SaveSecure(
        string inputPath,
        string outputPath,
        IReadOnlyCollection<PdfEditElement> edits,
        JobWorkspace workspace,
        IProgress<JobProgress>? progress,
        CancellationToken token)
    {
        const int dpi = 300;
        using var source = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
        using var output = new PdfSharp.Pdf.PdfDocument();
        EnsureValidPageIndexes(edits, source.PageCount);
        var byPage = edits.GroupBy(edit => edit.PageIndex).ToDictionary(group => group.Key, group => group.ToArray());
        var rendered = PdfRenderingService.RenderPages(
                inputPath,
                workspace.PathFor("secure-source"),
                dpi,
                byPage.Keys,
                token)
            .ToDictionary(page => page.Index);

        for (var pageIndex = 0; pageIndex < source.PageCount; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (!byPage.TryGetValue(pageIndex, out var pageEdits))
            {
                output.AddPage(source.Pages[pageIndex]);
            }
            else
            {
                var composedPdf = workspace.PathFor($"secure-composed-{pageIndex:D4}.pdf");
                using (var composed = new PdfSharp.Pdf.PdfDocument())
                {
                    var composedPage = composed.AddPage();
                    composedPage.Width = source.Pages[pageIndex].Width;
                    composedPage.Height = source.Pages[pageIndex].Height;
                    using var graphics = XGraphics.FromPdfPage(composedPage);
                    using var background = XImage.FromFile(rendered[pageIndex].ImagePath);
                    graphics.DrawImage(background, 0, 0, composedPage.Width.Point, composedPage.Height.Point);
                    DrawEdits(graphics, composedPage.Width.Point, composedPage.Height.Point, pageEdits, true);
                    composed.Save(composedPdf);
                }

                var flattened = PdfRenderingService.Render(
                    composedPdf,
                    workspace.PathFor($"secure-flat-{pageIndex:D4}"),
                    dpi,
                    token)[0];
                var page = output.AddPage();
                page.Width = source.Pages[pageIndex].Width;
                page.Height = source.Pages[pageIndex].Height;
                using var pageGraphics = XGraphics.FromPdfPage(page);
                using var flattenedImage = XImage.FromFile(flattened.ImagePath);
                pageGraphics.DrawImage(flattenedImage, 0, 0, page.Width.Point, page.Height.Point);
            }
            progress?.Report(new JobProgress((pageIndex + 1d) / source.PageCount * 95,
                $"正在安全重建 PDF 页面 {pageIndex + 1}/{source.PageCount}"));
        }
        output.Save(outputPath);
    }

    private static void DrawEdits(
        XGraphics graphics,
        double pageWidth,
        double pageHeight,
        IEnumerable<PdfEditElement> edits,
        bool coverOriginalText)
    {
        foreach (var edit in edits)
        {
            var rectangle = new XRect(
                Clamp(edit.X) * pageWidth,
                Clamp(edit.Y) * pageHeight,
                Math.Max(1, Clamp(edit.Width) * pageWidth),
                Math.Max(1, Clamp(edit.Height) * pageHeight));
            if (coverOriginalText && edit.Kind == PdfEditKind.TextReplacement)
                graphics.DrawRectangle(XBrushes.White, rectangle);

            if (edit.Kind == PdfEditKind.Image)
            {
                if (string.IsNullOrWhiteSpace(edit.ImagePath) || !File.Exists(edit.ImagePath))
                    throw new FileNotFoundException("找不到编辑中引用的图片。", edit.ImagePath);
                using var image = XImage.FromFile(edit.ImagePath);
                graphics.DrawImage(image, rectangle);
                continue;
            }

            if (edit.Kind == PdfEditKind.Formula)
            {
                DrawFormula(graphics, edit, rectangle);
                continue;
            }

            DrawText(graphics, edit, rectangle);
        }
    }

    private static void EnsureValidPageIndexes(IEnumerable<PdfEditElement> edits, int pageCount)
    {
        if (edits.Any(edit => edit.PageIndex < 0 || edit.PageIndex >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(edits), "编辑对象指向了不存在的 PDF 页面。");
    }

    private static void DrawText(XGraphics graphics, PdfEditElement edit, XRect rectangle)
    {
        if (string.IsNullOrEmpty(edit.Text)) return;
        var color = XColor.FromArgb(
            (byte)(edit.ColorArgb >> 24),
            (byte)(edit.ColorArgb >> 16),
            (byte)(edit.ColorArgb >> 8),
            (byte)edit.ColorArgb);
        var requestedSize = Math.Clamp(edit.FontSize, 5, 144);
        var lines = edit.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var size = requestedSize;
        XFont font;
        do
        {
            font = new XFont(edit.FontFamily, size, XFontStyleEx.Regular);
            var widest = lines.Max(line => graphics.MeasureString(string.IsNullOrEmpty(line) ? " " : line, font).Width);
            var totalHeight = lines.Length * size * 1.25;
            if ((widest <= rectangle.Width && totalHeight <= rectangle.Height) || size <= 5) break;
            size -= 0.5;
        } while (true);

        var brush = new XSolidBrush(color);
        var lineHeight = size * 1.25;
        for (var index = 0; index < lines.Length; index++)
        {
            var y = rectangle.Y + index * lineHeight;
            if (y + lineHeight > rectangle.Bottom + 0.5) break;
            graphics.DrawString(lines[index], font, brush,
                new XRect(rectangle.X, y, rectangle.Width, lineHeight), XStringFormats.TopLeft);
        }
    }

    private static void DrawFormula(XGraphics graphics, PdfEditElement edit, XRect rectangle)
    {
        if (string.IsNullOrWhiteSpace(edit.Text)) return;
        var vectorPdf = LatexFormulaService.RenderVectorPdf(
            edit.Text,
            edit.FontSize,
            rectangle.Width,
            rectangle.Height);
        using var stream = new MemoryStream(vectorPdf, writable: false);
        using var form = XPdfForm.FromStream(stream);
        graphics.DrawImage(form, rectangle);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}
