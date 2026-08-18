using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConvert.Core;
using DocConvert.Infrastructure.Windows;
using Microsoft.Win32;

namespace DocConvert.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly OutputPathService _paths = new();
    private readonly DocumentJobRunner _runner;
    private readonly IReadOnlyList<IWatermarkDetectionEngine> _detectors;
    private IReadOnlyList<string> _previewPagePaths = [];
    private bool _previewIsTiff;
    private int _previewLoadVersion;
    private CancellationTokenSource? _cancellation;

    public MainViewModel()
    {
        var openXml = new OpenXmlWatermarkEngine();
        var pdfWatermark = new PdfWatermarkEngine();
        _runner = new DocumentJobRunner(new IDocumentEngine[]
        {
            new OcrConversionEngine(), new PdfToPptxEngine(), new OfficeConversionEngine(), new PdfCreationEngine(),
            openXml, pdfWatermark, new ImageWatermarkRemovalEngine()
        });
        _detectors = [openXml, pdfWatermark, new ImageWatermarkDetectionEngine()];
        OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public ObservableCollection<JobEntryViewModel> ConversionJobs { get; } = [];
    public ObservableCollection<WatermarkFileViewModel> WatermarkFiles { get; } = [];
    public ObservableCollection<WatermarkCandidateViewModel> Candidates { get; } = [];
    public ObservableCollection<WatermarkRegion> Regions { get; } = [];
    public IReadOnlyList<string> TargetFormats { get; } = ["PDF", "DOCX", "PPTX"];
    public IReadOnlyList<int> DpiOptions { get; } = [150, 200, 300];
    public IReadOnlyList<WatermarkScopeOption> WatermarkScopes { get; } =
    [
        new("当前页 / 帧", WatermarkScope.CurrentPage),
        new("指定页码范围", WatermarkScope.PageRange),
        new("全部页面 / 帧", WatermarkScope.AllPages)
    ];

    [ObservableProperty] private string selectedTargetFormat = "PDF";
    [ObservableProperty] private string outputFolder = string.Empty;
    [ObservableProperty] private bool enableOcr;
    [ObservableProperty] private int renderDpi = 200;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "就绪";
    [ObservableProperty] private WatermarkFileViewModel? selectedWatermarkFile;
    [ObservableProperty] private BitmapSource? previewImage;
    [ObservableProperty] private int previewPageIndex;
    [ObservableProperty] private string pageRange = string.Empty;
    [ObservableProperty] private WatermarkScopeOption selectedWatermarkScope = new("全部页面 / 帧", WatermarkScope.AllPages);

    public bool IsPageRangeScope => SelectedWatermarkScope.Value == WatermarkScope.PageRange;
    public bool IsPptOutput => SelectedTargetFormat.Equals("PPTX", StringComparison.OrdinalIgnoreCase);
    public bool IsOcrAvailable => !IsPptOutput;
    public string ConversionModeDescription => IsPptOutput
        ? "高清视觉模式 · 固定 16:9 · 页面完整居中，适合投影与讲解；页面内容不能逐元素编辑。"
        : EnableOcr
            ? "OCR 已启用，将识别扫描页中的简体中文和英文。"
            : "转换过程全程在本机完成，不会上传文件。";
    public bool HasPreviousPreviewPage => PreviewImage is not null && PreviewPageIndex > 0;
    public bool HasNextPreviewPage => PreviewImage is not null && PreviewPageIndex + 1 < (SelectedWatermarkFile?.PageCount ?? 1);
    public string PreviewPageText => PreviewImage is null ? string.Empty : $"{PreviewPageIndex + 1} / {SelectedWatermarkFile?.PageCount ?? 1}";

    partial void OnSelectedWatermarkScopeChanged(WatermarkScopeOption value)
    {
        if (value.Value != WatermarkScope.PageRange) PageRange = string.Empty;
        OnPropertyChanged(nameof(IsPageRangeScope));
    }

    partial void OnSelectedTargetFormatChanged(string value)
    {
        if (IsPptOutput) EnableOcr = false;
        OnPropertyChanged(nameof(IsPptOutput));
        OnPropertyChanged(nameof(IsOcrAvailable));
        OnPropertyChanged(nameof(ConversionModeDescription));
    }

    partial void OnEnableOcrChanged(bool value) => OnPropertyChanged(nameof(ConversionModeDescription));

    partial void OnPreviewPageIndexChanged(int value)
    {
        NotifyPreviewNavigationChanged();
    }

    partial void OnPreviewImageChanged(BitmapSource? value)
    {
        NotifyPreviewNavigationChanged();
    }

    partial void OnSelectedWatermarkFileChanged(WatermarkFileViewModel? value)
    {
        Candidates.Clear();
        Regions.Clear();
        PreviewPageIndex = 0;
        _ = LoadPreviewAsync(value);
    }

    [RelayCommand]
    private void AddConversionFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "支持的文件|*.pdf;*.docx;*.xlsx;*.pptx;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true) AddConversionPaths(dialog.FileNames);
    }

    [RelayCommand]
    private void AddWatermarkFiles()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "支持的文件|*.pdf;*.docx;*.xlsx;*.pptx;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true) AddWatermarkPaths(dialog.FileNames);
    }

    [RelayCommand]
    private void ChooseOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录", InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private async Task StartConversionAsync()
    {
        if (ConversionJobs.Count == 0 || IsBusy) return;
        var unsupported = ConversionJobs
            .Where(item => item.State is JobState.Waiting or JobState.Failed)
            .Where(item => !IsSupportedConversion(item.InputPath, SelectedTargetFormat, EnableOcr))
            .Select(item => item.FileName)
            .ToArray();
        if (unsupported.Length > 0)
        {
            StatusText = $"当前输出格式不支持：{string.Join("、", unsupported.Take(3))}{(unsupported.Length > 3 ? " 等" : string.Empty)}";
            return;
        }
        var missingOffice = ConversionJobs
            .Where(item => item.State is JobState.Waiting or JobState.Failed)
            .Select(item => (Item: item, Installed: OfficeAvailability.IsInstalledForRoute(item.InputPath, "." + SelectedTargetFormat.ToLowerInvariant(), out var requirement), Requirement: requirement))
            .Where(result => !result.Installed)
            .ToArray();
        if (missingOffice.Length > 0)
        {
            StatusText = $"{missingOffice[0].Item.FileName} 需要安装 Microsoft {missingOffice[0].Requirement}。";
            return;
        }
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            foreach (var job in ConversionJobs.Where(item => item.State is JobState.Waiting or JobState.Failed).ToArray())
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var output = BuildConversionOutput(job.InputPath);
                var request = new DocumentJobRequest
                {
                    JobId = Guid.NewGuid(), Kind = JobKind.Convert, InputPath = job.InputPath, OutputPath = output,
                    TargetExtension = Path.GetExtension(output),
                    Conversion = new ConversionOptions
                    {
                        EnableOcr = IsOcrAvailable && EnableOcr,
                        RenderDpi = RenderDpi,
                        OcrLanguages = "chi_sim+eng"
                    }
                };
                job.State = JobState.Running;
                var result = await _runner.RunAsync(request, new Progress<JobProgress>(value =>
                {
                    job.Progress = value.Percent;
                    job.Message = value.Message;
                    StatusText = value.Message;
                }), _cancellation.Token);
                ApplyResult(job, result);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "任务已取消";
            foreach (var item in ConversionJobs.Where(item => item.State == JobState.Running)) item.State = JobState.Cancelled;
        }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand] private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var item in ConversionJobs.Where(item => item.State is JobState.Completed or JobState.CompletedWithWarnings or JobState.Cancelled).ToArray())
            ConversionJobs.Remove(item);
    }

    [RelayCommand]
    private async Task DetectWatermarksAsync()
    {
        if (SelectedWatermarkFile is null || IsBusy) return;
        IsBusy = true;
        Candidates.Clear();
        try
        {
            var detector = _detectors.FirstOrDefault(item => item.CanInspect(SelectedWatermarkFile.Path));
            if (detector is null) { StatusText = "该格式暂不支持自动检测"; return; }
            var results = await detector.DetectAsync(SelectedWatermarkFile.Path,
                new Progress<JobProgress>(value => StatusText = value.Message), CancellationToken.None);
            foreach (var candidate in results) Candidates.Add(new WatermarkCandidateViewModel(candidate));
        }
        catch (Exception exception) { StatusText = exception.Message.Trim(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveWatermarkAsync()
    {
        if (SelectedWatermarkFile is null || IsBusy) return;
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var item = SelectedWatermarkFile;
        try
        {
            var confirmedCandidates = Candidates.Where(candidate => candidate.IsSelected).Select(candidate => candidate.Label).ToArray();
            var scope = SelectedWatermarkScope.Value;
            var scopedRegions = PageRangeParser.ApplyScope(
                Regions,
                scope,
                PageRange,
                PreviewPageIndex,
                Math.Max(1, item.PageCount));
            if (SupportedFiles.Office.Contains(Path.GetExtension(item.Path)) && confirmedCandidates.Length == 0)
            {
                StatusText = "请先检测并勾选要删除的 Office 原生水印候选。";
                return;
            }
            if (!SupportedFiles.Office.Contains(Path.GetExtension(item.Path)) && scopedRegions.Count == 0)
            {
                StatusText = "请先在预览中框选水印区域。";
                return;
            }
            var output = BuildWatermarkOutput(item.Path);
            var request = new DocumentJobRequest
            {
                JobId = Guid.NewGuid(), Kind = JobKind.RemoveWatermark, InputPath = item.Path, OutputPath = output,
                Watermark = new WatermarkOptions
                {
                    AutoDetect = false,
                    Scope = scope,
                    PageRange = PageRange,
                    Regions = scopedRegions,
                    ConfirmedCandidateLabels = confirmedCandidates
                }
            };
            item.State = JobState.Running;
            var result = await _runner.RunAsync(request, new Progress<JobProgress>(value =>
            {
                item.Progress = value.Percent;
                item.Message = value.Message;
                StatusText = value.Message;
            }), _cancellation.Token);
            ApplyResult(item, result);
        }
        catch (OperationCanceledException) { item.State = JobState.Cancelled; StatusText = "任务已取消"; }
        catch (Exception exception) { item.State = JobState.Failed; item.Message = exception.Message; StatusText = exception.Message; }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand] private void ClearRegions() => Regions.Clear();

    [RelayCommand]
    private void PreviousPreviewPage()
    {
        if (!HasPreviousPreviewPage) return;
        PreviewPageIndex--;
        ShowPreviewPage();
    }

    [RelayCommand]
    private void NextPreviewPage()
    {
        if (!HasNextPreviewPage) return;
        PreviewPageIndex++;
        ShowPreviewPage();
    }

    [RelayCommand]
    private void OpenOutput(object? parameter)
    {
        var path = parameter switch
        {
            JobEntryViewModel job => job.OutputPath,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public void AddConversionPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!ConversionJobs.Any(item => item.InputPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                ConversionJobs.Add(new JobEntryViewModel(path));
    }

    public void AddWatermarkPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!WatermarkFiles.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                WatermarkFiles.Add(new WatermarkFileViewModel(path));
        SelectedWatermarkFile ??= WatermarkFiles.FirstOrDefault();
    }

    public void AddRegion(double x, double y, double width, double height)
    {
        if (width < 0.005 || height < 0.005) return;
        Regions.Add(new WatermarkRegion(PreviewPageIndex, x, y, width, height));
        StatusText = $"已添加选区，共 {Regions.Count} 个";
    }

    private async Task LoadPreviewAsync(WatermarkFileViewModel? item)
    {
        var loadVersion = Interlocked.Increment(ref _previewLoadVersion);
        PreviewImage = null;
        _previewPagePaths = [];
        _previewIsTiff = false;
        if (item is null) return;
        try
        {
            var extension = Path.GetExtension(item.Path).ToLowerInvariant();
            if (SupportedFiles.IsImage(item.Path))
            {
                item.PageCount = CountImageFrames(item.Path);
                _previewPagePaths = [item.Path];
                _previewIsTiff = extension is ".tif" or ".tiff";
            }
            else if (extension == ".pdf")
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DocConvert", "Temp", "Preview", Guid.NewGuid().ToString("N"));
                var pages = await Task.Run(() => PdfRenderingService.Render(item.Path, directory, 120, CancellationToken.None));
                if (loadVersion != _previewLoadVersion || SelectedWatermarkFile != item) return;
                _previewPagePaths = pages.Select(page => page.ImagePath).ToArray();
                item.PageCount = pages.Count;
            }
            if (loadVersion != _previewLoadVersion || SelectedWatermarkFile != item) return;
            item.PreviewAvailable = _previewPagePaths.Count > 0;
            PreviewPageIndex = 0;
            ShowPreviewPage();
            StatusText = PreviewImage is null ? "Office 文件将按原生对象结构检测；嵌入图片中的水印暂不显示预览。" : "在预览上拖动鼠标框选水印区域";
        }
        catch (Exception exception) { StatusText = $"预览失败：{exception.Message}"; }
    }

    private void ShowPreviewPage()
    {
        if (SelectedWatermarkFile is null || _previewPagePaths.Count == 0)
        {
            PreviewImage = null;
            return;
        }

        PreviewPageIndex = Math.Clamp(PreviewPageIndex, 0, Math.Max(0, SelectedWatermarkFile.PageCount - 1));
        PreviewImage = _previewIsTiff
            ? LoadTiffFrame(_previewPagePaths[0], PreviewPageIndex)
            : LoadBitmap(_previewPagePaths[Math.Min(PreviewPageIndex, _previewPagePaths.Count - 1)]);
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadTiffFrame(string path, int frameIndex)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[Math.Clamp(frameIndex, 0, decoder.Frames.Count - 1)];
        var copy = new WriteableBitmap(frame);
        copy.Freeze();
        return copy;
    }

    private void NotifyPreviewNavigationChanged()
    {
        OnPropertyChanged(nameof(HasPreviousPreviewPage));
        OnPropertyChanged(nameof(HasNextPreviewPage));
        OnPropertyChanged(nameof(PreviewPageText));
    }

    private string BuildConversionOutput(string input)
    {
        Directory.CreateDirectory(OutputFolder);
        var extension = "." + SelectedTargetFormat.ToLowerInvariant();
        return _paths.GetUniquePath(Path.Combine(OutputFolder, Path.GetFileNameWithoutExtension(input) + extension));
    }

    private string BuildWatermarkOutput(string input)
    {
        Directory.CreateDirectory(OutputFolder);
        return _paths.GetUniquePath(Path.Combine(OutputFolder,
            Path.GetFileNameWithoutExtension(input) + "_无水印" + Path.GetExtension(input)));
    }

    private static int CountImageFrames(string path)
    {
        if (!Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase)) return 1;
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Count;
    }

    private static void ApplyResult(JobEntryViewModel item, JobResult result)
    {
        item.OutputPath = result.OutputPath;
        item.Error = result.Error ?? string.Empty;
        item.Message = result.Success ? (result.Warnings.Count > 0 ? "完成，但有警告" : "已完成") : result.Error ?? "失败";
        item.State = result.Success
            ? (result.Warnings.Count > 0 ? JobState.CompletedWithWarnings : JobState.Completed)
            : JobState.Failed;
        item.Progress = result.Success ? 100 : item.Progress;
        item.WarningText = string.Join("；", result.Warnings.Select(warning => warning.Message));
    }

    private static bool IsSupportedConversion(string inputPath, string targetFormat, bool enableOcr)
    {
        var input = Path.GetExtension(inputPath).ToLowerInvariant();
        var output = "." + targetFormat.ToLowerInvariant();
        return (input, output) switch
        {
            (".pdf", ".docx") => true,
            (".pdf", ".pptx") => true,
            (".pdf", ".pdf") => enableOcr,
            (".docx", ".pdf") or (".xlsx", ".pdf") or (".pptx", ".pdf") => true,
            (".jpg", ".pdf") or (".jpeg", ".pdf") or (".png", ".pdf") or (".bmp", ".pdf") or (".tif", ".pdf") or (".tiff", ".pdf") => true,
            (".txt", ".pdf") => true,
            _ => false
        };
    }
}

public partial class JobEntryViewModel : ObservableObject
{
    public JobEntryViewModel(string inputPath) { InputPath = inputPath; }
    public string InputPath { get; }
    public string FileName => Path.GetFileName(InputPath);
    public string FileType => Path.GetExtension(InputPath).TrimStart('.').ToUpperInvariant();
    [ObservableProperty] private JobState state = JobState.Waiting;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string message = "等待处理";
    [ObservableProperty] private string outputPath = string.Empty;
    [ObservableProperty] private string error = string.Empty;
    [ObservableProperty] private string warningText = string.Empty;
}

public sealed partial class WatermarkFileViewModel : JobEntryViewModel
{
    public WatermarkFileViewModel(string path) : base(path) { Path = path; }
    public string Path { get; }
    [ObservableProperty] private int pageCount = 1;
    [ObservableProperty] private bool previewAvailable;
}

public sealed partial class WatermarkCandidateViewModel(WatermarkCandidate candidate) : ObservableObject
{
    public string Label { get; } = candidate.Label;
    public string Kind { get; } = candidate.Kind;
    public string Confidence { get; } = $"{candidate.Confidence:P0}";
    [ObservableProperty] private bool isSelected;
}

public sealed record WatermarkScopeOption(string Label, WatermarkScope Value);
