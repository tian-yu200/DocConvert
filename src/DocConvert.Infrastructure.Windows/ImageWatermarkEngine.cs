using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocConvert.Core;
using OpenCvSharp;

namespace DocConvert.Infrastructure.Windows;

public sealed class ImageWatermarkDetectionEngine : IWatermarkDetectionEngine
{
    public bool CanInspect(string inputPath) => SupportedFiles.IsImage(inputPath);

    public Task<IReadOnlyList<WatermarkCandidate>> DetectAsync(string inputPath, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<WatermarkCandidate>>(() =>
        {
            using var image = Cv2.ImRead(inputPath, ImreadModes.Color);
            if (image.Empty()) return [];
            using var gray = new Mat();
            using var edges = new Mat();
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Canny(gray, edges, 80, 180);
            Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var candidates = contours
                .Select(Cv2.BoundingRect)
                .Where(rect => rect.Width > image.Width * 0.15 && rect.Height > image.Height * 0.03)
                .Where(rect => rect.Width * rect.Height < image.Width * image.Height * 0.45)
                .OrderByDescending(rect => rect.Width)
                .Take(5)
                .Select((rect, index) => new WatermarkCandidate(
                    $"image-{index}", "疑似叠加文字或图形", "图像候选", 0.62,
                    [0], new WatermarkRegion(0, rect.X / (double)image.Width, rect.Y / (double)image.Height,
                        rect.Width / (double)image.Width, rect.Height / (double)image.Height)))
                .ToArray();
            progress?.Report(new JobProgress(100, candidates.Length == 0 ? "未发现高置信度候选" : $"发现 {candidates.Length} 个候选，请确认后处理"));
            return candidates;
        }, cancellationToken);
}

public sealed class ImageWatermarkRemovalEngine : IWatermarkRemovalEngine
{
    public string Name => "OpenCV 图像去水印引擎";

    public bool CanHandle(DocumentJobRequest request) => request.Kind == JobKind.RemoveWatermark && SupportedFiles.IsImage(request.InputPath);

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Remove(request, progress, cancellationToken), cancellationToken);

    private static JobResult Remove(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        DocumentSafety.EnsureModifiable(request.InputPath);
        if (request.Watermark.Regions.Count == 0)
            return JobResult.Fail(request.OutputPath, "请先框选水印区域或确认自动候选；应用不会自动删除未确认内容。");

        using var workspace = new JobWorkspace(request.JobId);
        var extension = Path.GetExtension(request.InputPath).ToLowerInvariant();
        var temporary = workspace.PathFor("cleaned" + extension);
        if (extension is ".tif" or ".tiff") ProcessTiff(request, temporary, workspace, progress, token);
        else ProcessFrame(request.InputPath, temporary, request.Watermark.Regions.Where(region => region.PageIndex == 0), token);
        workspace.Commit(temporary, request.OutputPath);
        return JobResult.Ok(request.OutputPath,
            [new JobWarning("INPAINT", "局部修复会根据周围像素推断内容；复杂纹理或覆盖正文的水印可能留痕。")]);
    }

    private static void ProcessTiff(DocumentJobRequest request, string outputPath, JobWorkspace workspace, IProgress<JobProgress>? progress, CancellationToken token)
    {
        using var source = File.OpenRead(request.InputPath);
        var decoder = new TiffBitmapDecoder(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var outputFrames = new List<BitmapFrame>();
        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var raw = workspace.PathFor($"frame-{index}.png");
            var cleaned = workspace.PathFor($"frame-{index}-cleaned.png");
            var png = new PngBitmapEncoder();
            png.Frames.Add(decoder.Frames[index]);
            using (var stream = File.Create(raw)) png.Save(stream);
            ProcessFrame(raw, cleaned, request.Watermark.Regions.Where(region => region.PageIndex == index), token);
            using var result = File.OpenRead(cleaned);
            outputFrames.Add(BitmapFrame.Create(result, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
            progress?.Report(new JobProgress((index + 1d) / decoder.Frames.Count * 95, $"正在处理 TIFF 帧 {index + 1}/{decoder.Frames.Count}"));
        }

        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        foreach (var frame in outputFrames) encoder.Frames.Add(frame);
        using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    internal static void ProcessFrame(string inputPath, string outputPath, IEnumerable<WatermarkRegion> regions, CancellationToken token)
    {
        using var image = Cv2.ImRead(inputPath, ImreadModes.Unchanged);
        if (image.Empty()) throw new InvalidOperationException("无法读取图像。");
        using var mask = new Mat(image.Rows, image.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            var rect = ToPixelRect(region, image.Width, image.Height);
            Cv2.Rectangle(mask, rect, Scalar.White, -1, LineTypes.Link8);
        }
        if (Cv2.CountNonZero(mask) == 0)
            throw new InvalidOperationException("水印选区没有覆盖有效像素。");
        using var expanded = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.Dilate(mask, expanded, kernel);
        using var bgr = image.Channels() == 4 ? new Mat() : image.Clone();
        if (image.Channels() == 4) Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR);
        using var cleaned = new Mat();
        Cv2.Inpaint(bgr, expanded, cleaned, 4, InpaintTypes.Telea);
        if (image.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(image, alpha, 3);
            using var restored = new Mat();
            Cv2.CvtColor(cleaned, restored, ColorConversionCodes.BGR2BGRA);
            Cv2.InsertChannel(alpha, restored, 3);
            if (!Cv2.ImWrite(outputPath, restored)) throw new IOException("无法写入处理后的图像。");
        }
        else if (!Cv2.ImWrite(outputPath, cleaned))
        {
            throw new IOException("无法写入处理后的图像。");
        }
    }

    internal static Rect ToPixelRect(WatermarkRegion region, int width, int height)
    {
        var x = region.IsNormalized ? region.X * width : region.X;
        var y = region.IsNormalized ? region.Y * height : region.Y;
        var w = region.IsNormalized ? region.Width * width : region.Width;
        var h = region.IsNormalized ? region.Height * height : region.Height;
        var left = Math.Clamp((int)Math.Round(x), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Round(y), 0, Math.Max(0, height - 1));
        return new Rect(
            left,
            top,
            Math.Clamp((int)Math.Round(w), 1, width - left),
            Math.Clamp((int)Math.Round(h), 1, height - top));
    }
}
