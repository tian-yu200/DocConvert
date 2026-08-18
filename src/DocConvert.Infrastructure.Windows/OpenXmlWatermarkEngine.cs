using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocConvert.Core;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using X = DocumentFormat.OpenXml.Spreadsheet;

namespace DocConvert.Infrastructure.Windows;

public sealed class OpenXmlWatermarkEngine : IWatermarkRemovalEngine, IWatermarkDetectionEngine
{
    private static readonly string[] Markers = ["watermark", "水印", "draft", "草稿", "confidential", "机密", "样张", "内部资料"];

    public string Name => "Office 原生去水印引擎";

    public bool CanHandle(DocumentJobRequest request) => request.Kind == JobKind.RemoveWatermark && SupportedFiles.Office.Contains(Path.GetExtension(request.InputPath));
    public bool CanInspect(string inputPath) => SupportedFiles.Office.Contains(Path.GetExtension(inputPath));

    public Task<IReadOnlyList<WatermarkCandidate>> DetectAsync(string inputPath, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<WatermarkCandidate>>(() =>
        {
            DocumentSafety.EnsureModifiable(inputPath);
            var extension = Path.GetExtension(inputPath).ToLowerInvariant();
            var labels = extension switch
            {
                ".docx" => DetectWord(inputPath),
                ".pptx" => DetectPowerPoint(inputPath),
                ".xlsx" => DetectExcel(inputPath),
                _ => []
            };
            progress?.Report(new JobProgress(100, labels.Count == 0 ? "未发现明确的原生水印对象" : $"发现 {labels.Count} 个原生水印候选"));
            return labels.Select((label, index) => new WatermarkCandidate($"office-{index}", label, "Office 原生对象", 0.92, [])).ToArray();
        }, cancellationToken);

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Remove(request, progress, cancellationToken), cancellationToken);

    private static JobResult Remove(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        DocumentSafety.EnsureModifiable(request.InputPath);
        if (request.Watermark.ConfirmedCandidateLabels.Count == 0)
            return JobResult.Fail(request.OutputPath, "请先检测并勾选要删除的 Office 原生水印对象。");
        using var workspace = new JobWorkspace(request.JobId);
        var temporary = workspace.PathFor("office" + Path.GetExtension(request.InputPath));
        File.Copy(request.InputPath, temporary, true);
        token.ThrowIfCancellationRequested();
        var count = Path.GetExtension(request.InputPath).ToLowerInvariant() switch
        {
            ".docx" => RemoveWord(temporary, request.Watermark.ConfirmedCandidateLabels),
            ".pptx" => RemovePowerPoint(temporary, request.Watermark.ConfirmedCandidateLabels),
            ".xlsx" => RemoveExcel(temporary, request.Watermark.ConfirmedCandidateLabels),
            _ => 0
        };
        if (count == 0) return JobResult.Fail(request.OutputPath, "未发现与已确认候选匹配的原生水印对象。嵌入图片中的水印请使用框选修复模式。");
        workspace.Commit(temporary, request.OutputPath);
        progress?.Report(new JobProgress(100, $"已删除 {count} 个明确的原生水印对象"));
        return JobResult.Ok(request.OutputPath);
    }

    private static List<string> DetectWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart!.HeaderParts.SelectMany(part => part.RootElement!.Descendants())
            .Where(IsWatermarkElement).Select(LabelFor).Distinct().ToList();
    }

    private static int RemoveWord(string path, IReadOnlyList<string> confirmed)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var elements = document.MainDocumentPart!.HeaderParts.SelectMany(part => part.RootElement!.Descendants())
            .Where(IsWatermarkElement).Where(element => MatchesConfirmed(element, confirmed)).Where(element => element.Parent is not null).ToList();
        foreach (var element in elements) element.Remove();
        return elements.Count;
    }

    private static List<string> DetectPowerPoint(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        return EnumeratePresentationElements(document).Where(IsWatermarkElement).Select(LabelFor).Distinct().ToList();
    }

    private static int RemovePowerPoint(string path, IReadOnlyList<string> confirmed)
    {
        using var document = PresentationDocument.Open(path, true);
        var elements = EnumeratePresentationElements(document).Where(IsWatermarkElement)
            .Where(element => MatchesConfirmed(element, confirmed)).Where(element => element.Parent is not null).ToList();
        foreach (var element in elements) element.Remove();
        return elements.Count;
    }

    private static IEnumerable<OpenXmlElement> EnumeratePresentationElements(PresentationDocument document)
    {
        var part = document.PresentationPart
            ?? throw new InvalidDataException("PowerPoint 文件缺少演示文稿主体。");
        foreach (var slide in part.SlideParts)
            if (slide.Slide is not null)
                foreach (var element in slide.Slide.Descendants<P.Shape>()) yield return element;
        foreach (var master in part.SlideMasterParts)
        {
            if (master.SlideMaster is not null)
                foreach (var element in master.SlideMaster.Descendants<P.Shape>()) yield return element;
            foreach (var layout in master.SlideLayoutParts)
                if (layout.SlideLayout is not null)
                    foreach (var element in layout.SlideLayout.Descendants<P.Shape>()) yield return element;
        }
    }

    private static List<string> DetectExcel(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Excel 文件缺少工作簿主体。");
        var candidates = new List<string>();
        foreach (var part in workbookPart.WorksheetParts)
        {
            if (part.Worksheet is null) continue;
            var header = part.Worksheet.Elements<X.HeaderFooter>().FirstOrDefault();
            if (header is not null && ContainsMarker(header.InnerText)) candidates.Add("工作表页眉/页脚水印");
            if (part.Worksheet.Elements<X.Picture>().Any()) candidates.Add("工作表背景图片");
            if (part.DrawingsPart?.WorksheetDrawing?.Descendants<A.NonVisualDrawingProperties>().Any(property => ContainsMarker(property.Name?.Value) || ContainsMarker(property.Description?.Value)) == true)
                candidates.Add("工作表浮动水印对象");
        }
        return candidates.Distinct().ToList();
    }

    private static int RemoveExcel(string path, IReadOnlyList<string> confirmed)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Excel 文件缺少工作簿主体。");
        var count = 0;
        foreach (var part in workbookPart.WorksheetParts)
        {
            if (part.Worksheet is null) continue;
            var headers = part.Worksheet.Elements<X.HeaderFooter>()
                .Where(header => ContainsMarker(header.InnerText) && confirmed.Contains("工作表页眉/页脚水印", StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var header in headers) { header.Remove(); count++; }
            var pictures = confirmed.Contains("工作表背景图片", StringComparer.OrdinalIgnoreCase)
                ? part.Worksheet.Elements<X.Picture>().ToList() : [];
            foreach (var picture in pictures) { picture.Remove(); count++; }
            var shapes = part.DrawingsPart?.WorksheetDrawing?.Descendants<OpenXmlCompositeElement>()
                .Where(element => element.Descendants<A.NonVisualDrawingProperties>()
                    .Any(property => ContainsMarker(property.Name?.Value) || ContainsMarker(property.Description?.Value)))
                .Where(element => confirmed.Contains("工作表浮动水印对象", StringComparer.OrdinalIgnoreCase))
                .Where(element => element.Parent is not null).ToList() ?? [];
            foreach (var shape in shapes) { shape.Remove(); count++; }
            part.Worksheet.Save();
        }
        return count;
    }

    private static bool IsWatermarkElement(OpenXmlElement element)
    {
        var text = element.InnerText;
        var name = element.Descendants<A.NonVisualDrawingProperties>().FirstOrDefault()?.Name?.Value;
        var description = element.Descendants<A.NonVisualDrawingProperties>().FirstOrDefault()?.Description?.Value;
        var attributes = string.Join(" ", element.GetAttributes().Select(attribute => attribute.Value));
        return ContainsMarker(text) || ContainsMarker(name) || ContainsMarker(description)
            || ContainsMarker(attributes)
            || (element.LocalName == "shape" && attributes.Contains("_x0000_t136", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsMarker(string? value) => !string.IsNullOrWhiteSpace(value)
        && Markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesConfirmed(OpenXmlElement element, IReadOnlyList<string> confirmed)
    {
        var label = LabelFor(element);
        return confirmed.Any(candidate => candidate.Equals(label, StringComparison.OrdinalIgnoreCase));
    }

    private static string LabelFor(OpenXmlElement element)
    {
        var text = element.InnerText.Trim();
        return string.IsNullOrWhiteSpace(text) ? $"{element.LocalName} 水印对象" : text[..Math.Min(text.Length, 40)];
    }
}
