using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DocConvert.Core;
using DocConvert.Infrastructure.Windows;

namespace DocConvert.App;

public partial class PdfEditorSurface : UserControl
{
    private const double ResizeHandleSize = 12;
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<string, BitmapSource> _formulaPreviewCache = new();
    private MainViewModel? _viewModel;
    private Point? _dragStart;
    private PdfEditElement? _dragSnapshot;
    private Point? _creationStart;
    private Point? _creationCurrent;
    private Point? _creationStartViewport;
    private PdfEditorTool _creationTool;
    private Point? _panStart;
    private Point _panSnapshot;
    private bool _resizing;
    private bool _panning;
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private int _displayedPageIndex = -1;
    private string _displayedPath = string.Empty;

    public PdfEditorSurface()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _renderTimer.Tick += RenderTimer_Tick;
        DataContextChanged += Surface_DataContextChanged;
        Loaded += Surface_Loaded;
        Unloaded += Surface_Unloaded;
    }

    private void Surface_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Unsubscribe();
        if (IsLoaded) Subscribe(e.NewValue as MainViewModel);
    }

    private void Surface_Loaded(object sender, RoutedEventArgs e) => Subscribe(DataContext as MainViewModel);

    private void Subscribe(MainViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(_viewModel, viewModel)) return;
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.PdfEdits.CollectionChanged += PdfEdits_CollectionChanged;
        foreach (var edit in _viewModel.PdfEdits) edit.PropertyChanged += Edit_PropertyChanged;
        ResetViewport();
    }

    private void Surface_Unloaded(object sender, RoutedEventArgs e)
    {
        CancelPointerInteraction();
        Unsubscribe();
    }

    private void Unsubscribe()
    {
        _renderTimer.Stop();
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.PdfEdits.CollectionChanged -= PdfEdits_CollectionChanged;
        foreach (var edit in _viewModel.PdfEdits) edit.PropertyChanged -= Edit_PropertyChanged;
        _viewModel = null;
    }

    private void CancelPointerInteraction()
    {
        _panning = false;
        _panStart = null;
        _creationStart = null;
        _creationCurrent = null;
        _creationStartViewport = null;
        _dragStart = null;
        _dragSnapshot = null;
        _resizing = false;
        if (ViewportCanvas.IsMouseCaptured) ViewportCanvas.ReleaseMouseCapture();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PdfEditorPageIndex) or nameof(MainViewModel.PdfEditorPath))
        {
            if (_viewModel is not null && (_displayedPageIndex != _viewModel.PdfEditorPageIndex || _displayedPath != _viewModel.PdfEditorPath))
                ResetViewport();
            return;
        }
        if (e.PropertyName is nameof(MainViewModel.PdfEditorPreviewImage)
            or nameof(MainViewModel.SelectedPdfEdit)
            or nameof(MainViewModel.SelectedPdfEditorTool)
            or nameof(MainViewModel.CurrentPdfTextBlocks))
            Dispatcher.BeginInvoke(() =>
            {
                UpdateCursor();
                UpdateViewport();
            });
    }

    private void PdfEdits_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (PdfEditElementViewModel edit in e.OldItems) edit.PropertyChanged -= Edit_PropertyChanged;
        if (e.NewItems is not null)
            foreach (PdfEditElementViewModel edit in e.NewItems) edit.PropertyChanged += Edit_PropertyChanged;
        Redraw();
    }

    private void Edit_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Redraw();

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) Surface_MouseMiddleButtonDown(sender, e);
        else if (e.ChangedButton == MouseButton.Left) Surface_MouseLeftButtonDown(sender, e);
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) Surface_MouseMiddleButtonUp(sender, e);
        else if (e.ChangedButton == MouseButton.Left) Surface_MouseLeftButtonUp(sender, e);
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.PdfEditorPreviewImage is null) return;
        Focus();
        if (Keyboard.IsKeyDown(Key.Space))
        {
            BeginPan(e.GetPosition(ViewportCanvas));
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(ViewportCanvas);
        if (!TryNormalize(point, out var normalizedX, out var normalizedY))
        {
            if (_viewModel.SelectedPdfEditorTool == PdfEditorTool.Select && _zoom > 1.001)
            {
                BeginPan(point);
                e.Handled = true;
            }
            return;
        }
        if (_viewModel.SelectedPdfEditorTool is PdfEditorTool.AddText or PdfEditorTool.AddFormula)
        {
            BeginCreation(_viewModel.SelectedPdfEditorTool, point, normalizedX, normalizedY);
            e.Handled = true;
            return;
        }
        if (_viewModel.SelectedPdfEditorTool == PdfEditorTool.ReplaceText)
        {
            _viewModel.ReplacePdfTextAt(normalizedX, normalizedY);
            Redraw();
            return;
        }

        var hit = _viewModel.FindPdfEditAt(normalizedX, normalizedY);
        _viewModel.SelectPdfEdit(hit);
        if (hit is null)
        {
            if (_zoom > 1.001)
            {
                BeginPan(point);
                e.Handled = true;
                return;
            }
            Redraw();
            return;
        }

        var bounds = GetEditBounds(hit);
        _resizing = Math.Abs(point.X - bounds.Right) <= ResizeHandleSize * 1.6
            && Math.Abs(point.Y - bounds.Bottom) <= ResizeHandleSize * 1.6;
        _viewModel.BeginPdfEditGeometryChange();
        _dragStart = new Point(normalizedX, normalizedY);
        _dragSnapshot = hit.ToModel();
        ViewportCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void BeginCreation(PdfEditorTool tool, Point viewportPoint, double normalizedX, double normalizedY)
    {
        _creationTool = tool;
        _creationStart = new Point(normalizedX, normalizedY);
        _creationCurrent = _creationStart;
        _creationStartViewport = viewportPoint;
        Cursor = Cursors.Cross;
        ViewportCanvas.CaptureMouse();
        Redraw();
    }

    private void Surface_MouseMiddleButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel?.PdfEditorPreviewImage is null) return;
        Focus();
        BeginPan(e.GetPosition(ViewportCanvas));
        e.Handled = true;
    }

    private void BeginPan(Point point)
    {
        _panning = true;
        _panStart = point;
        _panSnapshot = new Point(_panX, _panY);
        Cursor = Cursors.ScrollAll;
        ViewportCanvas.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panning && _panStart is not null)
        {
            var current = e.GetPosition(ViewportCanvas);
            _panX = _panSnapshot.X + current.X - _panStart.Value.X;
            _panY = _panSnapshot.Y + current.Y - _panStart.Value.Y;
            ClampPan();
            UpdateViewport();
            return;
        }
        if (_creationStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            _creationCurrent = NormalizeClamped(e.GetPosition(ViewportCanvas));
            Redraw();
            return;
        }
        if (_viewModel is null || _dragStart is null || _dragSnapshot is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(ViewportCanvas);
        var normalized = NormalizeClamped(point);
        var x = normalized.X;
        var y = normalized.Y;
        _viewModel.SetSelectedPdfEditGeometry(_dragSnapshot, x - _dragStart.Value.X, y - _dragStart.Value.Y, _resizing);
        Redraw();
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) EndPan();
        else if (_creationStart is not null) CompleteCreation(e.GetPosition(ViewportCanvas));
        else CompleteEditDrag();
    }

    private void Surface_MouseMiddleButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) EndPan();
    }

    private void EndPan()
    {
        _panning = false;
        _panStart = null;
        ViewportCanvas.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void CompleteCreation(Point viewportPoint)
    {
        if (_viewModel is null || _creationStart is null || _creationStartViewport is null) return;
        _creationCurrent = NormalizeClamped(viewportPoint);
        var bounds = PdfViewportMath.GetSelectionBounds(
            _creationStart.Value.X, _creationStart.Value.Y,
            _creationCurrent.Value.X, _creationCurrent.Value.Y);
        var dragged = (viewportPoint - _creationStartViewport.Value).Length >= 6;
        if (!dragged)
        {
            bounds = _creationTool == PdfEditorTool.AddFormula
                ? new PdfNormalizedRect(_creationStart.Value.X, _creationStart.Value.Y, 0.32, 0.12)
                : new PdfNormalizedRect(_creationStart.Value.X, _creationStart.Value.Y, 0.3, 0.08);
        }
        else
        {
            bounds = bounds with { Width = Math.Max(0.02, bounds.Width), Height = Math.Max(0.02, bounds.Height) };
        }

        PdfEditElementViewModel? created = _creationTool == PdfEditorTool.AddFormula
            ? _viewModel.AddPdfFormula(bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : _viewModel.AddPdfText(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        ClearCreation();
        if (created is not null) _viewModel.SelectedPdfEditorTool = PdfEditorTool.Select;
        Redraw();
    }

    private void ClearCreation()
    {
        _creationStart = null;
        _creationCurrent = null;
        _creationStartViewport = null;
        ViewportCanvas.ReleaseMouseCapture();
        UpdateCursor();
    }

    private void CompleteEditDrag()
    {
        if (_dragStart is null) return;
        ViewportCanvas.ReleaseMouseCapture();
        _dragStart = null;
        _dragSnapshot = null;
        _resizing = false;
        _viewModel?.CompletePdfEditGeometryChange();
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel?.PdfEditorPreviewImage is null) return;
        SetZoom(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(ViewportCanvas));
        e.Handled = true;
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25, ViewportCenter());
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25, ViewportCenter());
    private void Fit_Click(object sender, RoutedEventArgs e) => ResetViewport();

    private void SetZoom(double zoom, Point anchor)
    {
        zoom = Math.Clamp(zoom, PdfViewportMath.MinimumZoom, PdfViewportMath.MaximumZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001) return;
        var oldBounds = GetPageBounds();
        _zoom = zoom;
        var unpanned = GetPageBounds(0, 0);
        (_panX, _panY) = PdfViewportMath.ZoomAroundPoint(
            oldBounds, unpanned.Width, unpanned.Height,
            ViewportCanvas.ActualWidth, ViewportCanvas.ActualHeight, anchor.X, anchor.Y);
        ClampPan();
        UpdateViewport();
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async void RenderTimer_Tick(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        if (_viewModel is not null) await _viewModel.RequestPdfEditorRenderAsync(_zoom);
    }

    private void Surface_KeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.Key == Key.Delete)
        {
            _viewModel.DeleteSelectedPdfEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.UndoPdfEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.RedoPdfEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Add or Key.OemPlus)
        {
            SetZoom(_zoom * 1.25, ViewportCenter());
            e.Handled = true;
        }
        else if (e.Key is Key.Subtract or Key.OemMinus)
        {
            SetZoom(_zoom / 1.25, ViewportCenter());
            e.Handled = true;
        }
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClampPan();
        UpdateViewport();
    }

    private void ResetViewport()
    {
        _renderTimer.Stop();
        if (_creationStart is not null) ClearCreation();
        _zoom = 1;
        _panX = 0;
        _panY = 0;
        _displayedPageIndex = _viewModel?.PdfEditorPageIndex ?? -1;
        _displayedPath = _viewModel?.PdfEditorPath ?? string.Empty;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (_viewModel?.PdfEditorPreviewImage is null) return;
        var bounds = GetPageBounds();
        PageFrame.Width = bounds.Width;
        PageFrame.Height = bounds.Height;
        Canvas.SetLeft(PageFrame, bounds.X);
        Canvas.SetTop(PageFrame, bounds.Y);
        OverlayCanvas.Width = bounds.Width;
        OverlayCanvas.Height = bounds.Height;
        Canvas.SetLeft(OverlayCanvas, bounds.X);
        Canvas.SetTop(OverlayCanvas, bounds.Y);
        ZoomText.Text = $"{_zoom * 100:0}%";
        Redraw();
    }

    private void Redraw()
    {
        OverlayCanvas.Children.Clear();
        if (_viewModel?.PdfEditorPreviewImage is null) return;
        var pageBounds = new Rect(0, 0, OverlayCanvas.Width, OverlayCanvas.Height);

        if (_viewModel.SelectedPdfEditorTool == PdfEditorTool.ReplaceText)
        {
            foreach (var block in _viewModel.CurrentPdfTextBlocks)
            {
                var bounds = ToCanvasBounds(block.X, block.Y, block.Width, block.Height, pageBounds);
                var outline = new Rectangle
                {
                    Width = bounds.Width, Height = bounds.Height,
                    Stroke = new SolidColorBrush(Color.FromArgb(125, 21, 152, 163)), StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(24, 21, 152, 163))
                };
                Canvas.SetLeft(outline, bounds.X);
                Canvas.SetTop(outline, bounds.Y);
                OverlayCanvas.Children.Add(outline);
            }
        }

        foreach (var edit in _viewModel.PdfEdits.Where(edit => edit.PageIndex == _viewModel.PdfEditorPageIndex))
        {
            var bounds = GetEditBoundsRelative(edit);
            FrameworkElement content;
            if (edit.Kind == PdfEditKind.Image && !string.IsNullOrWhiteSpace(edit.ImagePath))
            {
                try { content = new Image { Source = new BitmapImage(new Uri(edit.ImagePath)), Stretch = Stretch.Fill, Opacity = 0.96 }; }
                catch { content = new Border { Background = Brushes.LightGray }; }
            }
            else if (edit.Kind == PdfEditKind.Formula)
            {
                content = CreateFormulaPreview(edit, bounds);
            }
            else
            {
                content = new Border
                {
                    Background = edit.Kind == PdfEditKind.TextReplacement ? Brushes.White : new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    Child = new TextBlock
                    {
                        Text = edit.Text,
                        FontSize = Math.Clamp(edit.FontSize * pageBounds.Height / Math.Max(1, _viewModel.CurrentPdfPageHeightPoints), 4, 240),
                        Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2)
                    }
                };
            }
            content.Width = bounds.Width;
            content.Height = bounds.Height;
            Canvas.SetLeft(content, bounds.X);
            Canvas.SetTop(content, bounds.Y);
            OverlayCanvas.Children.Add(content);

            var selected = edit == _viewModel.SelectedPdfEdit;
            var outline = new Rectangle
            {
                Width = bounds.Width, Height = bounds.Height,
                Stroke = selected ? new SolidColorBrush(Color.FromRgb(242, 91, 61)) : new SolidColorBrush(Color.FromArgb(150, 21, 152, 163)),
                StrokeThickness = selected ? 2 : 1, StrokeDashArray = selected ? null : [4, 3]
            };
            Canvas.SetLeft(outline, bounds.X);
            Canvas.SetTop(outline, bounds.Y);
            OverlayCanvas.Children.Add(outline);

            if (selected)
            {
                var handle = new Rectangle
                {
                    Width = ResizeHandleSize, Height = ResizeHandleSize, RadiusX = 2, RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(242, 91, 61)), Stroke = Brushes.White, StrokeThickness = 1
                };
                Canvas.SetLeft(handle, bounds.Right - ResizeHandleSize / 2);
                Canvas.SetTop(handle, bounds.Bottom - ResizeHandleSize / 2);
                OverlayCanvas.Children.Add(handle);
            }
        }

        if (_creationStart is not null && _creationCurrent is not null)
        {
            var selection = PdfViewportMath.GetSelectionBounds(
                _creationStart.Value.X, _creationStart.Value.Y,
                _creationCurrent.Value.X, _creationCurrent.Value.Y);
            var bounds = ToCanvasBounds(selection.X, selection.Y, selection.Width, selection.Height, pageBounds);
            var outline = new Rectangle
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Stroke = new SolidColorBrush(Color.FromRgb(242, 91, 61)),
                StrokeThickness = 2,
                StrokeDashArray = [5, 3],
                Fill = new SolidColorBrush(Color.FromArgb(24, 242, 91, 61))
            };
            Canvas.SetLeft(outline, bounds.X);
            Canvas.SetTop(outline, bounds.Y);
            OverlayCanvas.Children.Add(outline);
        }
    }

    private FrameworkElement CreateFormulaPreview(PdfEditElementViewModel edit, Rect bounds)
    {
        try
        {
            var width = Math.Clamp((int)Math.Ceiling(bounds.Width / 32) * 32, 64, 1600);
            var height = Math.Clamp((int)Math.Ceiling(bounds.Height / 32) * 32, 32, 800);
            var key = $"{edit.Text}\n{edit.FontSize:0.###}\n{width}x{height}";
            if (!_formulaPreviewCache.TryGetValue(key, out var bitmap))
            {
                var bytes = LatexFormulaService.RenderPng(edit.Text, edit.FontSize, width, height);
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                bitmap = image;
                if (_formulaPreviewCache.Count > 64) _formulaPreviewCache.Clear();
                _formulaPreviewCache[key] = bitmap;
            }
            return new Image { Source = bitmap, Stretch = Stretch.Fill };
        }
        catch
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(30, 220, 38, 38)),
                Child = new TextBlock { Text = "LaTeX", Foreground = Brushes.Firebrick, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            };
        }
    }

    private Rect GetEditBounds(PdfEditElementViewModel edit)
    {
        var relative = GetEditBoundsRelative(edit);
        var page = GetPageBounds();
        return new Rect(page.X + relative.X, page.Y + relative.Y, relative.Width, relative.Height);
    }

    private Rect GetEditBoundsRelative(PdfEditElementViewModel edit) =>
        ToCanvasBounds(edit.X, edit.Y, edit.Width, edit.Height, new Rect(0, 0, OverlayCanvas.Width, OverlayCanvas.Height));

    private static Rect ToCanvasBounds(double x, double y, double width, double height, Rect pageBounds) =>
        new(pageBounds.X + x * pageBounds.Width, pageBounds.Y + y * pageBounds.Height, width * pageBounds.Width, height * pageBounds.Height);

    private bool TryNormalize(Point point, out double x, out double y)
    {
        var bounds = GetPageBounds();
        if (point.X < bounds.X || point.X > bounds.Right || point.Y < bounds.Y || point.Y > bounds.Bottom)
        {
            x = y = 0;
            return false;
        }
        x = Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1);
        y = Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1);
        return true;
    }

    private Point NormalizeClamped(Point point)
    {
        var bounds = GetPageBounds();
        return new Point(
            Math.Clamp((point.X - bounds.X) / Math.Max(1, bounds.Width), 0, 1),
            Math.Clamp((point.Y - bounds.Y) / Math.Max(1, bounds.Height), 0, 1));
    }

    private PdfViewportRect GetPageBounds(double? panX = null, double? panY = null)
    {
        var pageWidth = _viewModel?.CurrentPdfPageWidthPoints ?? 595;
        var pageHeight = _viewModel?.CurrentPdfPageHeightPoints ?? 842;
        return PdfViewportMath.GetPageBounds(
            ViewportCanvas.ActualWidth, ViewportCanvas.ActualHeight,
            pageWidth, pageHeight, _zoom, panX ?? _panX, panY ?? _panY);
    }

    private void ClampPan()
    {
        var unpanned = GetPageBounds(0, 0);
        (_panX, _panY) = PdfViewportMath.ClampPan(
            ViewportCanvas.ActualWidth, ViewportCanvas.ActualHeight,
            unpanned.Width, unpanned.Height, _panX, _panY);
    }

    private void UpdateCursor()
    {
        if (_panning) return;
        Cursor = _viewModel?.SelectedPdfEditorTool is PdfEditorTool.AddText or PdfEditorTool.AddFormula
            ? Cursors.Cross
            : Cursors.Arrow;
    }

    private Point ViewportCenter() => new(ViewportCanvas.ActualWidth / 2, ViewportCanvas.ActualHeight / 2);
}
