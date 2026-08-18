using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DocConvert.App;

public partial class MainWindow : Window
{
    private Point? _selectionStart;
    private Rectangle? _selectionRectangle;
    private MainViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
        Closed += MainWindow_Closed;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var watermarkExtensions = new[] { ".pdf", ".docx", ".xlsx", ".pptx", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
        ViewModel.AddConversionPaths(paths);
        ViewModel.AddWatermarkPaths(paths.Where(path => watermarkExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)));
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImageControl.Source is null) return;
        _selectionStart = e.GetPosition(SelectionCanvas);
        _selectionRectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(48, 220, 38, 38))
        };
        SelectionCanvas.Children.Add(_selectionRectangle);
        PreviewHost.CaptureMouse();
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selectionStart is null || _selectionRectangle is null || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(SelectionCanvas);
        var x = Math.Min(_selectionStart.Value.X, current.X);
        var y = Math.Min(_selectionStart.Value.Y, current.Y);
        Canvas.SetLeft(_selectionRectangle, x);
        Canvas.SetTop(_selectionRectangle, y);
        _selectionRectangle.Width = Math.Abs(current.X - _selectionStart.Value.X);
        _selectionRectangle.Height = Math.Abs(current.Y - _selectionStart.Value.Y);
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        PreviewHost.ReleaseMouseCapture();
        if (_selectionStart is null || _selectionRectangle is null || ViewModel is null) return;
        var imageBounds = GetDisplayedImageBounds();
        var left = Canvas.GetLeft(_selectionRectangle);
        var top = Canvas.GetTop(_selectionRectangle);
        var right = left + _selectionRectangle.Width;
        var bottom = top + _selectionRectangle.Height;
        var clippedLeft = Math.Max(left, imageBounds.X);
        var clippedTop = Math.Max(top, imageBounds.Y);
        var clippedRight = Math.Min(right, imageBounds.Right);
        var clippedBottom = Math.Min(bottom, imageBounds.Bottom);
        if (clippedRight > clippedLeft && clippedBottom > clippedTop)
        {
            ViewModel.AddRegion(
                (clippedLeft - imageBounds.X) / imageBounds.Width,
                (clippedTop - imageBounds.Y) / imageBounds.Height,
                (clippedRight - clippedLeft) / imageBounds.Width,
                (clippedBottom - clippedTop) / imageBounds.Height);
        }
        _selectionStart = null;
        _selectionRectangle = null;
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromViewModel();
        _subscribedViewModel = e.NewValue as MainViewModel;
        if (_subscribedViewModel is null) return;
        _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _subscribedViewModel.Regions.CollectionChanged += Regions_CollectionChanged;
        RedrawRegions();
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => UnsubscribeFromViewModel();

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is null) return;
        _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribedViewModel.Regions.CollectionChanged -= Regions_CollectionChanged;
        _subscribedViewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PreviewImage) or nameof(MainViewModel.PreviewPageIndex))
            Dispatcher.BeginInvoke(RedrawRegions);
    }

    private void Regions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawRegions();

    private void SelectionCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawRegions();

    private void RedrawRegions()
    {
        SelectionCanvas.Children.Clear();
        if (ViewModel is null || PreviewImageControl.Source is null) return;
        var imageBounds = GetDisplayedImageBounds();
        foreach (var region in ViewModel.Regions.Where(region => region.PageIndex == ViewModel.PreviewPageIndex))
        {
            var rectangle = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(48, 220, 38, 38)),
                Width = region.Width * imageBounds.Width,
                Height = region.Height * imageBounds.Height
            };
            Canvas.SetLeft(rectangle, imageBounds.X + region.X * imageBounds.Width);
            Canvas.SetTop(rectangle, imageBounds.Y + region.Y * imageBounds.Height);
            SelectionCanvas.Children.Add(rectangle);
        }
    }

    private Rect GetDisplayedImageBounds()
    {
        if (PreviewImageControl.Source is not System.Windows.Media.Imaging.BitmapSource bitmap)
            return new Rect(0, 0, SelectionCanvas.ActualWidth, SelectionCanvas.ActualHeight);
        var availableWidth = Math.Max(1, SelectionCanvas.ActualWidth);
        var availableHeight = Math.Max(1, SelectionCanvas.ActualHeight);
        var scale = Math.Min(availableWidth / bitmap.PixelWidth, availableHeight / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        return new Rect((availableWidth - width) / 2, (availableHeight - height) / 2, width, height);
    }
}
