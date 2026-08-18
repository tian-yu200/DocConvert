using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DocConvert.Core;

namespace DocConvert.Infrastructure.Windows;

public sealed class PdfToImageEngine : IConversionEngine
{
    public string Name => "PDF 转图片引擎";

    public bool CanHandle(DocumentJobRequest request)
    {
        if (request.Kind != JobKind.Convert
            || !Path.GetExtension(request.InputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetExtension(request.OutputPath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";
    }

    public Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Convert(request, progress, cancellationToken), cancellationToken);

    private static JobResult Convert(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken token)
    {
        using var workspace = new JobWorkspace(request.JobId);
        var rendered = PdfRenderingService.Render(
            request.InputPath,
            workspace.PathFor("rendered"),
            request.Conversion.RenderDpi,
            token);
        if (rendered.Count == 0) throw new InvalidDataException("PDF 中没有可转换的页面。");

        var extension = Path.GetExtension(request.OutputPath).ToLowerInvariant();
        var outputPaths = GetAvailableOutputPaths(request.OutputPath, rendered.Count);
        var temporaryPaths = new List<string>(rendered.Count);
        for (var index = 0; index < rendered.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var temporary = workspace.PathFor($"image-{index + 1:D4}{extension}");
            if (extension == ".png")
                File.Copy(rendered[index].ImagePath, temporary);
            else
                SaveJpeg(rendered[index].ImagePath, temporary);
            temporaryPaths.Add(temporary);
            progress?.Report(new JobProgress(
                (index + 1d) / rendered.Count * 95,
                $"正在生成图片 {index + 1}/{rendered.Count}"));
        }

        token.ThrowIfCancellationRequested();
        var committed = workspace.CommitBatch(temporaryPaths.Zip(outputPaths));
        progress?.Report(new JobProgress(100, $"已生成 {committed.Count} 张图片"));
        return JobResult.Ok(committed[0]);
    }

    private static void SaveJpeg(string sourcePath, string outputPath)
    {
        using var source = File.OpenRead(sourcePath);
        var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static IReadOnlyList<string> GetAvailableOutputPaths(string desiredPath, int pageCount)
    {
        var directory = Path.GetDirectoryName(desiredPath)
            ?? throw new InvalidOperationException("输出路径缺少目录。");
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var copyIndex = 0; copyIndex < 10_000; copyIndex++)
        {
            var candidateName = copyIndex == 0 ? name : $"{name} ({copyIndex})";
            var paths = Enumerable.Range(0, pageCount)
                .Select(pageIndex => Path.Combine(directory,
                    pageIndex == 0
                        ? candidateName + extension
                        : $"{candidateName}_{pageIndex + 1:D3}{extension}"))
                .ToArray();
            if (paths.All(path => !File.Exists(path))) return paths;
        }

        throw new IOException("无法为输出图片分配唯一名称。");
    }
}
