namespace DocConvert.Core;

public interface IDocumentEngine
{
    string Name { get; }
    bool CanHandle(DocumentJobRequest request);
    Task<JobResult> ExecuteAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}

public interface IConversionEngine : IDocumentEngine;
public interface IWatermarkRemovalEngine : IDocumentEngine;

public interface IWatermarkDetectionEngine
{
    bool CanInspect(string inputPath);
    Task<IReadOnlyList<WatermarkCandidate>> DetectAsync(string inputPath, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}

public interface IDocumentJobRunner
{
    Task<JobResult> RunAsync(DocumentJobRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}

public interface IOutputPathService
{
    string GetUniquePath(string desiredPath);
    string CreateWatermarkOutputPath(string inputPath);
    string CreateConvertedOutputPath(string inputPath, string extension);
}
