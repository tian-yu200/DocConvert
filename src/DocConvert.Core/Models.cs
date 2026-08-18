using System.Collections.ObjectModel;

namespace DocConvert.Core;

public enum JobKind { Convert, RemoveWatermark }
public enum JobState { Waiting, Running, Completed, CompletedWithWarnings, Failed, Cancelled }
public enum WatermarkScope { CurrentPage, PageRange, AllPages }

public sealed record WatermarkRegion(
    int PageIndex, double X, double Y, double Width, double Height, bool IsNormalized = true);

public sealed record ConversionOptions
{
    public int RenderDpi { get; init; } = 200;
    public bool EnableOcr { get; init; }
    public string OcrLanguages { get; init; } = "chi_sim+eng";
    public bool MergeImages { get; init; }
    public string? PageRange { get; init; }
}

public sealed record WatermarkOptions
{
    public WatermarkScope Scope { get; init; } = WatermarkScope.AllPages;
    public string? PageRange { get; init; }
    public bool AutoDetect { get; init; } = true;
    public IReadOnlyList<WatermarkRegion> Regions { get; init; } = [];
    public IReadOnlyList<string> ConfirmedCandidateLabels { get; init; } = [];
}

public sealed record DocumentJobRequest
{
    public required Guid JobId { get; init; }
    public required JobKind Kind { get; init; }
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public string? TargetExtension { get; init; }
    public ConversionOptions Conversion { get; init; } = new();
    public WatermarkOptions Watermark { get; init; } = new();
}

public sealed record JobProgress(double Percent, string Message);
public sealed record JobWarning(string Code, string Message);

public sealed record JobResult
{
    public required bool Success { get; init; }
    public required string OutputPath { get; init; }
    public IReadOnlyList<JobWarning> Warnings { get; init; } = [];
    public string? Error { get; init; }

    public static JobResult Ok(string path, IReadOnlyList<JobWarning>? warnings = null) => new()
    {
        Success = true,
        OutputPath = path,
        Warnings = warnings ?? []
    };

    public static JobResult Fail(string path, string error) => new()
    {
        Success = false,
        OutputPath = path,
        Error = error
    };
}

public sealed record WatermarkCandidate(
    string Id, string Label, string Kind, double Confidence,
    IReadOnlyList<int> Pages, WatermarkRegion? Region = null);

public sealed class JobQueueItem
{
    public required DocumentJobRequest Request { get; init; }
    public JobState State { get; set; } = JobState.Waiting;
    public double Progress { get; set; }
    public string Message { get; set; } = "等待处理";
    public string? Error { get; set; }
    public ObservableCollection<JobWarning> Warnings { get; } = [];
}
