using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocConvert.Core;
using DocConvert.Infrastructure.Windows;
using Microsoft.Win32;

namespace DocConvert.App;

public enum PdfEditorTool { Select, AddText, ReplaceText, AddFormula }

public sealed partial class MainViewModel
{
    private readonly Stack<IReadOnlyList<PdfEditElement>> _pdfUndo = new();
    private readonly Stack<IReadOnlyList<PdfEditElement>> _pdfRedo = new();
    private IReadOnlyList<string> _pdfEditorPagePaths = [];
    private IReadOnlyList<RenderedPdfPage> _pdfEditorPages = [];
    private IReadOnlyList<PdfTextBlock> _pdfTextBlocks = [];
    private IReadOnlyList<PdfEditElement>? _pdfGeometryUndoState;
    private readonly Dictionary<(int PageIndex, int Dpi), string> _pdfEditorRenderCache = new();
    private int _pdfEditorLoadVersion;
    private int _pdfEditorRenderVersion;
    private bool _restoringPdfEditState;
    private string _pdfTextCreationDraft = "新文字";
    private string _pdfFormulaCreationDraft = @"\frac{a+b}{c}";
    private double _pdfTextCreationFontSize = 12;
    private double _pdfFormulaCreationFontSize = 20;

    public ObservableCollection<PdfEditElementViewModel> PdfEdits { get; } = [];
    public IReadOnlyList<PdfTextBlock> CurrentPdfTextBlocks => _pdfTextBlocks
        .Where(block => block.PageIndex == PdfEditorPageIndex)
        .ToArray();

    [ObservableProperty] private string pdfEditorPath = string.Empty;
    [ObservableProperty] private BitmapSource? pdfEditorPreviewImage;
    [ObservableProperty] private int pdfEditorPageIndex;
    [ObservableProperty] private int pdfEditorPageCount;
    [ObservableProperty] private PdfEditorTool selectedPdfEditorTool = PdfEditorTool.Select;
    [ObservableProperty] private PdfEditElementViewModel? selectedPdfEdit;
    [ObservableProperty] private string pdfEditDraftText = "新文字";
    [ObservableProperty] private double pdfEditDraftFontSize = 12;
    [ObservableProperty] private BitmapSource? pdfFormulaPreviewImage;
    [ObservableProperty] private string pdfFormulaError = string.Empty;

    public bool HasPdfEditorDocument => !string.IsNullOrWhiteSpace(PdfEditorPath) && PdfEditorPreviewImage is not null;
    public bool HasPreviousPdfEditorPage => HasPdfEditorDocument && PdfEditorPageIndex > 0;
    public bool HasNextPdfEditorPage => HasPdfEditorDocument && PdfEditorPageIndex + 1 < PdfEditorPageCount;
    public string PdfEditorPageText => HasPdfEditorDocument ? $"{PdfEditorPageIndex + 1} / {PdfEditorPageCount}" : string.Empty;
    public string PdfEditorFileName => string.IsNullOrWhiteSpace(PdfEditorPath) ? "未打开 PDF" : Path.GetFileName(PdfEditorPath);
    public bool HasSelectedPdfEdit => SelectedPdfEdit is not null;
    public bool IsPdfCreationToolSelected => SelectedPdfEdit is null && SelectedPdfEditorTool is PdfEditorTool.AddText or PdfEditorTool.AddFormula;
    public bool CanEditSelectedPdfText => SelectedPdfEdit is { Kind: not PdfEditKind.Image } || IsPdfCreationToolSelected;
    public bool CanApplyPdfEditProperties => SelectedPdfEdit is { Kind: not PdfEditKind.Image };
    public bool SelectedPdfEditIsFormula => SelectedPdfEdit?.Kind == PdfEditKind.Formula
        || SelectedPdfEdit is null && SelectedPdfEditorTool == PdfEditorTool.AddFormula;
    public string PdfEditContentLabel => SelectedPdfEditIsFormula ? "LaTeX 公式" : "文字内容";
    public string PdfEditApplyLabel => SelectedPdfEditIsFormula ? "应用公式修改" : "应用文字修改";
    public string PdfEditPanelSubtitle => SelectedPdfEdit?.KindLabel
        ?? (SelectedPdfEditorTool == PdfEditorTool.AddFormula ? "新公式草稿"
            : SelectedPdfEditorTool == PdfEditorTool.AddText ? "新文字草稿" : "未选择对象");
    public bool HasPdfFormulaError => !string.IsNullOrWhiteSpace(PdfFormulaError);
    public bool CanUndoPdfEdit => _pdfUndo.Count > 0;
    public bool CanRedoPdfEdit => _pdfRedo.Count > 0;
    public bool HasPdfEdits => PdfEdits.Count > 0;
    public double CurrentPdfPageWidthPoints => _pdfEditorPages.Count == 0 ? 595 : _pdfEditorPages[Math.Clamp(PdfEditorPageIndex, 0, _pdfEditorPages.Count - 1)].WidthPoints;
    public double CurrentPdfPageHeightPoints => _pdfEditorPages.Count == 0 ? 842 : _pdfEditorPages[Math.Clamp(PdfEditorPageIndex, 0, _pdfEditorPages.Count - 1)].HeightPoints;

    partial void OnPdfEditorPageIndexChanged(int value)
    {
        ShowPdfEditorPage();
        SelectedPdfEdit = PdfEdits.FirstOrDefault(edit => edit.PageIndex == value && edit.Id == SelectedPdfEdit?.Id);
        NotifyPdfEditorStateChanged();
        OnPropertyChanged(nameof(CurrentPdfTextBlocks));
        Interlocked.Increment(ref _pdfEditorRenderVersion);
    }

    partial void OnPdfEditorPreviewImageChanged(BitmapSource? value) => NotifyPdfEditorStateChanged();

    partial void OnSelectedPdfEditChanged(PdfEditElementViewModel? value)
    {
        if (value is not null)
        {
            if (PdfEditorPageIndex != value.PageIndex) PdfEditorPageIndex = value.PageIndex;
            PdfEditDraftText = value.Text;
            PdfEditDraftFontSize = value.FontSize;
        }
        OnPropertyChanged(nameof(HasSelectedPdfEdit));
        OnPropertyChanged(nameof(CanEditSelectedPdfText));
        OnPropertyChanged(nameof(CanApplyPdfEditProperties));
        OnPropertyChanged(nameof(IsPdfCreationToolSelected));
        OnPropertyChanged(nameof(SelectedPdfEditIsFormula));
        OnPropertyChanged(nameof(PdfEditContentLabel));
        OnPropertyChanged(nameof(PdfEditApplyLabel));
        OnPropertyChanged(nameof(PdfEditPanelSubtitle));
        RefreshPdfFormulaPreview();
    }

    partial void OnPdfEditDraftTextChanged(string value)
    {
        if (SelectedPdfEdit is null)
        {
            if (SelectedPdfEditorTool == PdfEditorTool.AddText) _pdfTextCreationDraft = value;
            if (SelectedPdfEditorTool == PdfEditorTool.AddFormula) _pdfFormulaCreationDraft = value;
        }
        RefreshPdfFormulaPreview();
    }

    partial void OnPdfEditDraftFontSizeChanged(double value)
    {
        if (SelectedPdfEdit is null)
        {
            if (SelectedPdfEditorTool == PdfEditorTool.AddText) _pdfTextCreationFontSize = value;
            if (SelectedPdfEditorTool == PdfEditorTool.AddFormula) _pdfFormulaCreationFontSize = value;
        }
        RefreshPdfFormulaPreview();
    }

    partial void OnPdfFormulaErrorChanged(string value) => OnPropertyChanged(nameof(HasPdfFormulaError));

    partial void OnSelectedPdfEditorToolChanged(PdfEditorTool value)
    {
        OnPropertyChanged(nameof(CurrentPdfTextBlocks));
        OnPropertyChanged(nameof(CanEditSelectedPdfText));
        OnPropertyChanged(nameof(CanApplyPdfEditProperties));
        OnPropertyChanged(nameof(IsPdfCreationToolSelected));
        OnPropertyChanged(nameof(SelectedPdfEditIsFormula));
        OnPropertyChanged(nameof(PdfEditContentLabel));
        OnPropertyChanged(nameof(PdfEditApplyLabel));
        OnPropertyChanged(nameof(PdfEditPanelSubtitle));
        RefreshPdfFormulaPreview();
    }

    [RelayCommand]
    private async Task OpenPdfEditorDocumentAsync()
    {
        var dialog = new OpenFileDialog { Filter = "PDF 文件|*.pdf", Multiselect = false };
        if (dialog.ShowDialog() == true) await LoadPdfEditorDocumentAsync(dialog.FileName);
    }

    public async Task LoadPdfEditorDocumentAsync(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        var version = Interlocked.Increment(ref _pdfEditorLoadVersion);
        IsBusy = true;
        StatusText = "正在读取 PDF 编辑结构";
        try
        {
            DocumentSafety.EnsureModifiable(path);
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocConvert", "Temp", "EditorPreview", Guid.NewGuid().ToString("N"));
            var renderTask = Task.Run(() => PdfRenderingService.Render(path, directory, 144, CancellationToken.None));
            var textTask = Task.Run(() => PdfEditingService.ExtractTextBlocks(path));
            await Task.WhenAll(renderTask, textTask);
            if (version != _pdfEditorLoadVersion) return;

            PdfEditorPath = path;
            _pdfEditorPages = renderTask.Result;
            _pdfEditorPagePaths = _pdfEditorPages.Select(page => page.ImagePath).ToArray();
            _pdfEditorRenderCache.Clear();
            for (var index = 0; index < _pdfEditorPagePaths.Count; index++)
                _pdfEditorRenderCache[(index, 144)] = _pdfEditorPagePaths[index];
            _pdfTextBlocks = textTask.Result;
            PdfEditorPageCount = _pdfEditorPages.Count;
            PdfEditorPageIndex = 0;
            ClearPdfEditSession();
            ShowPdfEditorPage();
            OnPropertyChanged(nameof(PdfEditorFileName));
            OnPropertyChanged(nameof(CurrentPdfTextBlocks));
            StatusText = $"已打开 {Path.GetFileName(path)}，检测到 {_pdfTextBlocks.Count} 个文字块";
        }
        catch (Exception exception)
        {
            StatusText = $"无法打开 PDF：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyPdfEditorStateChanged();
        }
    }

    [RelayCommand]
    private void PreviousPdfEditorPage()
    {
        if (HasPreviousPdfEditorPage) PdfEditorPageIndex--;
    }

    [RelayCommand]
    private void NextPdfEditorPage()
    {
        if (HasNextPdfEditorPage) PdfEditorPageIndex++;
    }

    [RelayCommand]
    private void SelectPdfEditorTool(PdfEditorTool tool)
    {
        if (tool is PdfEditorTool.AddText or PdfEditorTool.AddFormula)
        {
            SelectedPdfEdit = null;
            SelectedPdfEditorTool = tool;
            if (tool == PdfEditorTool.AddFormula)
            {
                PdfEditDraftText = _pdfFormulaCreationDraft;
                PdfEditDraftFontSize = _pdfFormulaCreationFontSize;
            }
            else
            {
                PdfEditDraftText = _pdfTextCreationDraft;
                PdfEditDraftFontSize = _pdfTextCreationFontSize;
            }
            return;
        }
        SelectedPdfEditorTool = tool;
    }

    [RelayCommand]
    private void AddPdfImage()
    {
        if (!HasPdfEditorDocument) return;
        var dialog = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        PushPdfUndo();
        var edit = new PdfEditElementViewModel(new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Image, PageIndex = PdfEditorPageIndex,
            X = 0.32, Y = 0.32, Width = 0.36, Height = 0.24, ImagePath = dialog.FileName
        });
        PdfEdits.Add(edit);
        SelectedPdfEdit = edit;
        SelectedPdfEditorTool = PdfEditorTool.Select;
        CompletePdfMutation("已添加图片，可拖动或使用右下角手柄缩放");
    }

    [RelayCommand]
    private void ApplyPdfEditProperties()
    {
        if (SelectedPdfEdit is null || SelectedPdfEdit.Kind == PdfEditKind.Image) return;
        if (SelectedPdfEdit.Kind == PdfEditKind.Formula)
        {
            var error = LatexFormulaService.Validate(PdfEditDraftText);
            if (!string.IsNullOrWhiteSpace(error))
            {
                PdfFormulaError = error;
                StatusText = "LaTeX 公式存在语法错误";
                return;
            }
        }
        if (SelectedPdfEdit.Text == PdfEditDraftText && Math.Abs(SelectedPdfEdit.FontSize - PdfEditDraftFontSize) < 0.01) return;
        PushPdfUndo();
        SelectedPdfEdit.Text = PdfEditDraftText;
        SelectedPdfEdit.FontSize = Math.Clamp(PdfEditDraftFontSize, 5, 144);
        CompletePdfMutation(SelectedPdfEdit.Kind == PdfEditKind.Formula ? "公式属性已应用" : "文字属性已应用");
    }

    [RelayCommand]
    private void DeleteSelectedPdfEdit()
    {
        if (SelectedPdfEdit is null) return;
        PushPdfUndo();
        PdfEdits.Remove(SelectedPdfEdit);
        SelectedPdfEdit = null;
        CompletePdfMutation("已删除编辑对象");
    }

    [RelayCommand]
    private void UndoPdfEdit()
    {
        if (_pdfUndo.Count == 0) return;
        _pdfRedo.Push(CapturePdfEditState());
        RestorePdfEditState(_pdfUndo.Pop());
        StatusText = "已撤销上一步 PDF 编辑";
    }

    [RelayCommand]
    private void RedoPdfEdit()
    {
        if (_pdfRedo.Count == 0) return;
        _pdfUndo.Push(CapturePdfEditState());
        RestorePdfEditState(_pdfRedo.Pop());
        StatusText = "已重做 PDF 编辑";
    }

    [RelayCommand]
    private Task SavePdfOverlayAsync() => SavePdfEditorAsync(PdfEditSaveMode.Overlay);

    [RelayCommand]
    private Task SavePdfNativeAsync() => SavePdfEditorAsync(PdfEditSaveMode.NativeContent);

    [RelayCommand]
    private Task SavePdfSecureAsync() => SavePdfEditorAsync(PdfEditSaveMode.SecureRasterized);

    public PdfEditElementViewModel AddPdfText(double x, double y, double width = 0.3, double height = 0.08)
    {
        PushPdfUndo();
        var edit = new PdfEditElementViewModel(new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Text, PageIndex = PdfEditorPageIndex,
            X = Clamp(x), Y = Clamp(y), Width = width, Height = height,
            Text = string.IsNullOrWhiteSpace(PdfEditDraftText) ? "新文字" : PdfEditDraftText,
            FontSize = Math.Clamp(PdfEditDraftFontSize, 5, 144)
        });
        KeepInsidePage(edit);
        PdfEdits.Add(edit);
        SelectedPdfEdit = edit;
        CompletePdfMutation("已添加文字，可在右侧修改内容");
        return edit;
    }

    public PdfEditElementViewModel? AddPdfFormula(double x, double y, double width = 0.32, double height = 0.12)
    {
        var latex = string.IsNullOrWhiteSpace(PdfEditDraftText) ? @"\frac{a+b}{c}" : PdfEditDraftText;
        var error = LatexFormulaService.Validate(latex);
        if (!string.IsNullOrWhiteSpace(error))
        {
            PdfFormulaError = error;
            StatusText = "请先修正 LaTeX 公式语法";
            return null;
        }
        PushPdfUndo();
        var edit = new PdfEditElementViewModel(new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.Formula, PageIndex = PdfEditorPageIndex,
            X = Clamp(x), Y = Clamp(y), Width = width, Height = height,
            Text = latex, FontSize = Math.Clamp(PdfEditDraftFontSize, 5, 144)
        });
        KeepInsidePage(edit);
        PdfEdits.Add(edit);
        SelectedPdfEdit = edit;
        CompletePdfMutation("已添加 LaTeX 公式，可在右侧继续编辑");
        return edit;
    }

    public async Task RequestPdfEditorRenderAsync(double zoom)
    {
        if (!HasPdfEditorDocument || _pdfEditorPages.Count == 0) return;
        var pageIndex = PdfEditorPageIndex;
        var page = _pdfEditorPages[pageIndex];
        var dpi = PdfViewportMath.SelectRenderDpi(zoom, page.WidthPoints, page.HeightPoints);
        if (_pdfEditorRenderCache.TryGetValue((pageIndex, dpi), out var cached))
        {
            PdfEditorPreviewImage = LoadBitmap(cached);
            return;
        }

        var path = PdfEditorPath;
        var version = Interlocked.Increment(ref _pdfEditorRenderVersion);
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocConvert", "Temp", "EditorPreview", $"{Path.GetFileNameWithoutExtension(path)}-{pathHash}-{pageIndex:D4}-{dpi}");
        try
        {
            var rendered = await Task.Run(() => PdfRenderingService.RenderPages(
                path, directory, dpi, [pageIndex], CancellationToken.None));
            if (version != _pdfEditorRenderVersion || pageIndex != PdfEditorPageIndex || path != PdfEditorPath) return;
            var imagePath = rendered[0].ImagePath;
            _pdfEditorRenderCache[(pageIndex, dpi)] = imagePath;
            PdfEditorPreviewImage = LoadBitmap(imagePath);
        }
        catch (Exception exception)
        {
            StatusText = $"高清页面渲染失败：{exception.Message}";
        }
    }

    public PdfEditElementViewModel? ReplacePdfTextAt(double x, double y)
    {
        var block = CurrentPdfTextBlocks
            .Where(item => Contains(item.X, item.Y, item.Width, item.Height, x, y))
            .OrderBy(item => item.Width * item.Height)
            .FirstOrDefault();
        if (block is null)
        {
            StatusText = "当前位置没有检测到可替换的文字块";
            return null;
        }

        PushPdfUndo();
        var edit = new PdfEditElementViewModel(new PdfEditElement
        {
            Id = Guid.NewGuid(), Kind = PdfEditKind.TextReplacement, PageIndex = PdfEditorPageIndex,
            X = Clamp(block.X - 0.002), Y = Clamp(block.Y - 0.002),
            Width = Math.Min(1, block.Width + 0.004), Height = Math.Min(1, Math.Max(block.Height + 0.006, 0.025)),
            Text = block.Text, OriginalText = block.Text, FontSize = Math.Clamp(block.FontSize, 5, 144),
            SourceX = Clamp(block.X - 0.002), SourceY = Clamp(block.Y - 0.002),
            SourceWidth = Math.Min(1, block.Width + 0.004), SourceHeight = Math.Min(1, block.Height + 0.004)
        });
        PdfEdits.Add(edit);
        SelectedPdfEdit = edit;
        PdfEditDraftText = block.Text;
        PdfEditDraftFontSize = edit.FontSize;
        SelectedPdfEditorTool = PdfEditorTool.Select;
        CompletePdfMutation("已创建文字替换，请在右侧修改后点击应用");
        return edit;
    }

    public PdfEditElementViewModel? FindPdfEditAt(double x, double y) => PdfEdits
        .Where(edit => edit.PageIndex == PdfEditorPageIndex && Contains(edit.X, edit.Y, edit.Width, edit.Height, x, y))
        .LastOrDefault();

    public void SelectPdfEdit(PdfEditElementViewModel? edit) => SelectedPdfEdit = edit;

    public void BeginPdfEditGeometryChange()
    {
        _pdfGeometryUndoState = SelectedPdfEdit is null ? null : CapturePdfEditState();
    }

    public void SetSelectedPdfEditGeometry(PdfEditElement snapshot, double deltaX, double deltaY, bool resize)
    {
        if (SelectedPdfEdit is null || SelectedPdfEdit.Id != snapshot.Id) return;
        if (resize)
        {
            SelectedPdfEdit.Width = Math.Clamp(snapshot.Width + deltaX, 0.02, 1 - snapshot.X);
            SelectedPdfEdit.Height = Math.Clamp(snapshot.Height + deltaY, 0.02, 1 - snapshot.Y);
        }
        else
        {
            SelectedPdfEdit.X = Math.Clamp(snapshot.X + deltaX, 0, 1 - snapshot.Width);
            SelectedPdfEdit.Y = Math.Clamp(snapshot.Y + deltaY, 0, 1 - snapshot.Height);
        }
    }

    public void CompletePdfEditGeometryChange()
    {
        var previous = _pdfGeometryUndoState;
        _pdfGeometryUndoState = null;
        if (previous is null || previous.SequenceEqual(CapturePdfEditState())) return;
        _pdfUndo.Push(previous);
        _pdfRedo.Clear();
        CompletePdfMutation("编辑对象位置已更新", clearRedo: false);
    }

    private async Task SavePdfEditorAsync(PdfEditSaveMode mode)
    {
        if (!HasPdfEditorDocument || !HasPdfEdits || IsBusy) return;
        var suffix = mode switch
        {
            PdfEditSaveMode.NativeContent => "_原生编辑",
            PdfEditSaveMode.SecureRasterized => "_安全编辑",
            _ => "_已编辑"
        };
        var dialog = new SaveFileDialog
        {
            Filter = "PDF 文件|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(PdfEditorPath) + suffix + ".pdf",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : Path.GetDirectoryName(PdfEditorPath)
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<JobProgress>(value => StatusText = value.Message);
        try
        {
            var models = CapturePdfEditState();
            await Task.Run(() => PdfEditingService.Save(
                PdfEditorPath, dialog.FileName, models, mode,
                progress, _cancellation.Token, File.Exists(dialog.FileName)), _cancellation.Token);
            StatusText = mode switch
            {
                PdfEditSaveMode.NativeContent => $"原生编辑副本已保存，原文字已从内容流删除：{dialog.FileName}",
                PdfEditSaveMode.SecureRasterized => $"安全编辑副本已保存：{dialog.FileName}",
                _ => $"普通编辑副本已保存：{dialog.FileName}"
            };
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) { StatusText = "PDF 保存已取消"; }
        catch (Exception exception) { StatusText = $"PDF 保存失败：{exception.Message}"; }
        finally
        {
            IsBusy = false;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void ShowPdfEditorPage()
    {
        if (_pdfEditorPagePaths.Count == 0)
        {
            PdfEditorPreviewImage = null;
            return;
        }
        PdfEditorPageIndex = Math.Clamp(PdfEditorPageIndex, 0, _pdfEditorPagePaths.Count - 1);
        PdfEditorPreviewImage = LoadBitmap(_pdfEditorRenderCache.TryGetValue((PdfEditorPageIndex, 144), out var path)
            ? path
            : _pdfEditorPagePaths[PdfEditorPageIndex]);
    }

    private void ClearPdfEditSession()
    {
        _restoringPdfEditState = true;
        try { PdfEdits.Clear(); }
        finally { _restoringPdfEditState = false; }
        _pdfUndo.Clear();
        _pdfRedo.Clear();
        _pdfGeometryUndoState = null;
        SelectedPdfEdit = null;
        NotifyPdfEditorStateChanged();
    }

    private void RefreshPdfFormulaPreview()
    {
        if (!SelectedPdfEditIsFormula)
        {
            PdfFormulaPreviewImage = null;
            PdfFormulaError = string.Empty;
            return;
        }
        try
        {
            var error = LatexFormulaService.Validate(PdfEditDraftText);
            PdfFormulaError = error ?? string.Empty;
            if (error is not null)
            {
                PdfFormulaPreviewImage = null;
                return;
            }
            var bytes = LatexFormulaService.RenderPng(PdfEditDraftText, PdfEditDraftFontSize, 480, 150);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            PdfFormulaPreviewImage = bitmap;
        }
        catch (Exception exception)
        {
            PdfFormulaPreviewImage = null;
            PdfFormulaError = exception.Message;
        }
    }

    private void PushPdfUndo()
    {
        if (_restoringPdfEditState) return;
        _pdfUndo.Push(CapturePdfEditState());
        _pdfRedo.Clear();
        NotifyPdfEditorStateChanged();
    }

    private IReadOnlyList<PdfEditElement> CapturePdfEditState() => PdfEdits.Select(edit => edit.ToModel()).ToArray();

    private void RestorePdfEditState(IReadOnlyList<PdfEditElement> state)
    {
        var selectedId = SelectedPdfEdit?.Id;
        _restoringPdfEditState = true;
        try
        {
            PdfEdits.Clear();
            foreach (var model in state) PdfEdits.Add(new PdfEditElementViewModel(model));
        }
        finally { _restoringPdfEditState = false; }
        SelectedPdfEdit = PdfEdits.FirstOrDefault(edit => edit.Id == selectedId);
        NotifyPdfEditorStateChanged();
    }

    private void CompletePdfMutation(string status, bool clearRedo = true)
    {
        if (clearRedo) _pdfRedo.Clear();
        StatusText = status;
        NotifyPdfEditorStateChanged();
    }

    private void NotifyPdfEditorStateChanged()
    {
        OnPropertyChanged(nameof(HasPdfEditorDocument));
        OnPropertyChanged(nameof(HasPreviousPdfEditorPage));
        OnPropertyChanged(nameof(HasNextPdfEditorPage));
        OnPropertyChanged(nameof(PdfEditorPageText));
        OnPropertyChanged(nameof(CanUndoPdfEdit));
        OnPropertyChanged(nameof(CanRedoPdfEdit));
        OnPropertyChanged(nameof(HasPdfEdits));
    }

    private static void KeepInsidePage(PdfEditElementViewModel edit)
    {
        edit.Width = Math.Clamp(edit.Width, 0.02, 1);
        edit.Height = Math.Clamp(edit.Height, 0.02, 1);
        edit.X = Math.Clamp(edit.X, 0, 1 - edit.Width);
        edit.Y = Math.Clamp(edit.Y, 0, 1 - edit.Height);
    }

    private static bool Contains(double left, double top, double width, double height, double x, double y) =>
        x >= left && x <= left + width && y >= top && y <= top + height;

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}

public sealed partial class PdfEditElementViewModel : ObservableObject
{
    public PdfEditElementViewModel(PdfEditElement model)
    {
        Id = model.Id;
        Kind = model.Kind;
        PageIndex = model.PageIndex;
        x = model.X;
        y = model.Y;
        width = model.Width;
        height = model.Height;
        text = model.Text;
        OriginalText = model.OriginalText;
        imagePath = model.ImagePath;
        fontFamily = model.FontFamily;
        fontSize = model.FontSize;
        colorArgb = model.ColorArgb;
        SourceX = model.SourceX;
        SourceY = model.SourceY;
        SourceWidth = model.SourceWidth;
        SourceHeight = model.SourceHeight;
    }

    public Guid Id { get; }
    public PdfEditKind Kind { get; }
    public int PageIndex { get; }
    public string OriginalText { get; }
    public double? SourceX { get; }
    public double? SourceY { get; }
    public double? SourceWidth { get; }
    public double? SourceHeight { get; }
    public int PageNumber => PageIndex + 1;
    public string DisplayName => Kind == PdfEditKind.Image
        ? Path.GetFileName(ImagePath) ?? "图片"
        : string.IsNullOrWhiteSpace(Text) ? KindLabel : Text;
    public string KindLabel => Kind switch
    {
        PdfEditKind.Text => "文字",
        PdfEditKind.Image => "图片",
        PdfEditKind.TextReplacement => "文字替换",
        _ => "LaTeX 公式"
    };

    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
    [ObservableProperty] private double width;
    [ObservableProperty] private double height;
    [ObservableProperty] private string text;
    [ObservableProperty] private string? imagePath;
    [ObservableProperty] private string fontFamily;
    [ObservableProperty] private double fontSize;
    [ObservableProperty] private uint colorArgb;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnImagePathChanged(string? value) => OnPropertyChanged(nameof(DisplayName));

    public PdfEditElement ToModel() => new()
    {
        Id = Id, Kind = Kind, PageIndex = PageIndex,
        X = X, Y = Y, Width = Width, Height = Height,
        Text = Text, OriginalText = OriginalText, ImagePath = ImagePath, FontFamily = FontFamily,
        FontSize = FontSize, ColorArgb = ColorArgb,
        SourceX = SourceX, SourceY = SourceY, SourceWidth = SourceWidth, SourceHeight = SourceHeight
    };
}
