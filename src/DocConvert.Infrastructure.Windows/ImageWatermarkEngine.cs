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
            using var image = OpenCvImageFile.Read(inputPath, ImreadModes.Color);
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
        using var image = OpenCvImageFile.Read(inputPath, ImreadModes.Unchanged);
        using var bgr = new Mat();
        if (image.Channels() == 4) Cv2.CvtColor(image, bgr, ColorConversionCodes.BGRA2BGR);
        else if (image.Channels() == 1) Cv2.CvtColor(image, bgr, ColorConversionCodes.GRAY2BGR);
        else image.CopyTo(bgr);
        using var prepared = bgr.Clone();
        using var mask = BuildSelectiveMask(bgr, prepared, regions, token, out var repairedDirectly);
        var maskedPixels = Cv2.CountNonZero(mask);
        if (maskedPixels == 0 && !repairedDirectly)
            throw new InvalidOperationException("选区内没有识别到可安全去除的浅色或半透明水印像素。深色不透明水印覆盖正文时无法无损恢复，请缩小选区后重试。");
        using var cleaned = new Mat();
        if (maskedPixels > 0) Cv2.Inpaint(prepared, mask, cleaned, 3, InpaintTypes.Telea);
        else prepared.CopyTo(cleaned);
        if (image.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(image, alpha, 3);
            using var restored = new Mat();
            Cv2.CvtColor(cleaned, restored, ColorConversionCodes.BGR2BGRA);
            Cv2.InsertChannel(alpha, restored, 3);
            OpenCvImageFile.Write(outputPath, restored);
        }
        else if (image.Channels() == 1)
        {
            using var grayscale = new Mat();
            Cv2.CvtColor(cleaned, grayscale, ColorConversionCodes.BGR2GRAY);
            OpenCvImageFile.Write(outputPath, grayscale);
        }
        else
        {
            OpenCvImageFile.Write(outputPath, cleaned);
        }
    }

    private static Mat BuildSelectiveMask(Mat bgr, Mat prepared, IEnumerable<WatermarkRegion> regions,
        CancellationToken token, out bool repairedDirectly)
    {
        repairedDirectly = false;
        var mask = new Mat(bgr.Rows, bgr.Cols, MatType.CV_8UC1, Scalar.Black);
        foreach (var region in regions)
        {
            token.ThrowIfCancellationRequested();
            var rect = ToPixelRect(region, bgr.Width, bgr.Height);
            repairedDirectly |= AddRegionToMask(bgr, prepared, mask, rect);
        }

        return mask;
    }

    private static bool AddRegionToMask(Mat bgr, Mat prepared, Mat destination, Rect rect)
    {
        var background = EstimateBackground(bgr, rect, out var backgroundCoverage);
        var backgroundLuminance = Luminance(background.Item0, background.Item1, background.Item2);
        var inkThreshold = Math.Clamp(backgroundLuminance * 0.42, 45, 110);
        using var candidates = new Mat(rect.Height, rect.Width, MatType.CV_8UC1, Scalar.Black);
        using var protectedInk = new Mat(rect.Height, rect.Width, MatType.CV_8UC1, Scalar.Black);

        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var pixel = bgr.At<Vec3b>(rect.Y + y, rect.X + x);
                var luminance = Luminance(pixel.Item0, pixel.Item1, pixel.Item2);
                var distance = Math.Max(Math.Abs(pixel.Item0 - background.Item0),
                    Math.Max(Math.Abs(pixel.Item1 - background.Item1), Math.Abs(pixel.Item2 - background.Item2)));
                if (distance < 8) continue;

                if (luminance <= inkThreshold)
                    protectedInk.Set(y, x, byte.MaxValue);
                else
                    candidates.Set(y, x, byte.MaxValue);
            }
        }

        using var candidateKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        using var expandedCandidates = new Mat();
        Cv2.Dilate(candidates, expandedCandidates, candidateKernel);

        // Keep a one-pixel buffer around strong document ink so inpainting cannot soften its edges.
        using var inkKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var expandedInk = new Mat();
        Cv2.Dilate(protectedInk, expandedInk, inkKernel);
        Cv2.BitwiseNot(expandedInk, expandedInk);
        Cv2.BitwiseAnd(expandedCandidates, expandedInk, expandedCandidates);
        if (Cv2.CountNonZero(expandedCandidates) == 0) return false;

        if (backgroundCoverage >= 0.6)
        {
            using var preparedRegion = new Mat(prepared, rect);
            preparedRegion.SetTo(new Scalar(background.Item0, background.Item1, background.Item2), expandedCandidates);
            return true;
        }

        using var destinationRegion = new Mat(destination, rect);
        Cv2.BitwiseOr(destinationRegion, expandedCandidates, destinationRegion);
        return false;
    }

    private static Vec3d EstimateBackground(Mat bgr, Rect rect, out double coverage)
    {
        var margin = Math.Clamp(Math.Min(rect.Width, rect.Height) / 4, 4, 32);
        var outerLeft = Math.Max(0, rect.Left - margin);
        var outerTop = Math.Max(0, rect.Top - margin);
        var outerRight = Math.Min(bgr.Width, rect.Right + margin);
        var outerBottom = Math.Min(bgr.Height, rect.Bottom + margin);
        var area = Math.Max(1, (outerRight - outerLeft) * (outerBottom - outerTop));
        var step = Math.Max(1, (int)Math.Sqrt(area / 20000d));
        var blue = new List<byte>();
        var green = new List<byte>();
        var red = new List<byte>();
        var samples = new List<Vec3b>();

        for (var y = outerTop; y < outerBottom; y += step)
        {
            for (var x = outerLeft; x < outerRight; x += step)
            {
                if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) continue;
                var pixel = bgr.At<Vec3b>(y, x);
                samples.Add(pixel);
                blue.Add(pixel.Item0);
                green.Add(pixel.Item1);
                red.Add(pixel.Item2);
            }
        }

        if (blue.Count < 32)
        {
            for (var y = rect.Top; y < rect.Bottom; y += step)
            for (var x = rect.Left; x < rect.Right; x += step)
            {
                var pixel = bgr.At<Vec3b>(y, x);
                samples.Add(pixel);
                blue.Add(pixel.Item0);
                green.Add(pixel.Item1);
                red.Add(pixel.Item2);
            }
        }

        blue.Sort();
        green.Sort();
        red.Sort();
        var index = Math.Clamp((int)Math.Round((blue.Count - 1) * 0.7), 0, blue.Count - 1);
        var background = new Vec3d(blue[index], green[index], red[index]);
        var matching = 0;
        foreach (var sample in samples)
        {
            if (Math.Abs(sample.Item0 - background.Item0) <= 18
                && Math.Abs(sample.Item1 - background.Item1) <= 18
                && Math.Abs(sample.Item2 - background.Item2) <= 18)
                matching++;
        }
        coverage = matching / (double)blue.Count;
        return background;
    }

    private static double Luminance(double blue, double green, double red) =>
        blue * 0.114 + green * 0.587 + red * 0.299;

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
