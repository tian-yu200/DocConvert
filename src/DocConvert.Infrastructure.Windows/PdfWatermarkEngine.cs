using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocConvert.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace DocConvert.Infrastructure.Windows;

public sealed class PdfWatermarkEngine : IWatermarkRemovalEngine, IWatermarkDetectionEngine
{
    public string Name => "PDF 安全区域去水印引擎";
    public bool CanInspect(string inputPath) => Path.GetExtension(inputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public bool CanHandle(DocumentJobRequest request) => request.Kind == JobKind.RemoveWatermark && CanInspect(request.InputPath);

    public Task<IReadOnlyList<WatermarkCandidate>> DetectAsync(string inputPath, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<WatermarkCandidate>>(() =>
        {
            DocumentSafety.EnsureModifiable(inputPath);
            using var document = UglyToad.PdfPig.PdfDocument.Open(inputPath);
            var perPage = Enumerable.Range(1, document.NumberOfPages)
                .Select(page => document.GetPage(page).GetWords().Select(word => Normalize(word.Text)).Where(text => text.Length >= 2).ToHashSet())
                .ToArray();
            if (perPage.Length == 0) return [];
            var threshold = perPage.Length <= 2 ? perPage.Length : (int)Math.Ceiling(perPage.Length * 0.6);
            var occurrences = new List<(string Word, int Page)>();
            for (var pageIndex = 0; pageIndex < perPage.Length; pageIndex++)
                occurrences.AddRange(perPage[pageIndex].Select(word => (word, pageIndex)));
            var repeated = occurrences
                .GroupBy(item => item.Word)
                .Where(group => group.Select(item => item.Page).Distinct().Count() >= threshold)
                .Where(group => group.Key.Contains("watermark", StringComparison.OrdinalIgnoreCase) || group.Key.Contains("水印")
                    || group.Key.Contains("draft", StringComparison.OrdinalIgnoreCase) || group.Key.Contains("机密"))
                .Take(10)
                .Select((group, index) => new WatermarkCandidate($"pdf-{index}", group.Key, "重复文字", 0.88,
                    group.Select(item => item.Page).Distinct().ToArray()))
                .ToArray();
            progress?.Report(new JobProgress(100, repeated.Length == 0 ? "未发现高置信度重复水印" : $"发现 {repeated.Length} 个候选，请框选确认"));
            return repeated;
        }, cancellationToken);

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Remove(request, progress, cancellationToken), cancellationToken);

    private static JobResult Remove(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        DocumentSafety.EnsureModifiable(request.InputPath);
        if (request.Watermark.Regions.Count == 0)
            return JobResult.Fail(request.OutputPath, "当前 PDF 无法安全定位到独立内容对象，请先在预览中框选水印区域。");

        using var workspace = new JobWorkspace(request.JobId);
        var pages = PdfRenderingService.Render(request.InputPath, workspace.PathFor("pages"), 300, token);
        var grouped = request.Watermark.Regions.GroupBy(region => region.PageIndex).ToDictionary(group => group.Key, group => group.AsEnumerable());
        var cleaned = new Dictionary<int, string>();
        foreach (var pair in grouped)
        {
            if (pair.Key < 0 || pair.Key >= pages.Count) continue;
            var output = workspace.PathFor($"cleaned-{pair.Key}.png");
            ImageWatermarkRemovalEngine.ProcessFrame(pages[pair.Key].ImagePath, output, pair.Value, token);
            cleaned[pair.Key] = output;
        }

        using var source = PdfReader.Open(request.InputPath, PdfDocumentOpenMode.Import);
        using var outputPdf = new PdfSharp.Pdf.PdfDocument();
        for (var index = 0; index < source.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!cleaned.TryGetValue(index, out var imagePath))
            {
                outputPdf.AddPage(source.Pages[index]);
            }
            else
            {
                var page = outputPdf.AddPage();
                // PDF rendering applies /Rotate. Rebuild processed pages using the rendered
                // display dimensions so rotated pages are not stretched into the raw MediaBox.
                page.Width = XUnit.FromPoint(pages[index].WidthPoints);
                page.Height = XUnit.FromPoint(pages[index].HeightPoints);
                using var graphics = XGraphics.FromPdfPage(page);
                using var image = XImage.FromFile(imagePath);
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            progress?.Report(new JobProgress((index + 1d) / source.PageCount * 95, $"正在重建 PDF 页面 {index + 1}/{source.PageCount}"));
        }

        var temporary = workspace.PathFor("cleaned.pdf");
        outputPdf.Save(temporary);
        workspace.Commit(temporary, request.OutputPath);
        return JobResult.Ok(request.OutputPath,
            [new JobWarning("PDF_RASTERIZED", "仅框选处理的 PDF 页面已栅格化；未处理页面仍保留原始矢量内容。复杂背景可能留痕。")]);
    }

    private static string Normalize(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character))).Trim();
}
