using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class OcrScannerConfigDialog : Window
{
    private Point? _dragStartPoint;
    private bool _isDraggingRoi;

    public OcrScannerConfigDialogViewModel ViewModel { get; }

    public OcrScannerConfigDialog(OcrScannerConfigDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePreviewImage();
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OcrScannerConfigDialogViewModel.OriginalSampleBytes)
            or nameof(OcrScannerConfigDialogViewModel.PreprocessedPreviewBytes)
            or nameof(OcrScannerConfigDialogViewModel.ShowPreprocessedPreview))
        {
            UpdatePreviewImage();
        }
        else if (e.PropertyName is nameof(OcrScannerConfigDialogViewModel.RoiX)
            or nameof(OcrScannerConfigDialogViewModel.RoiY)
            or nameof(OcrScannerConfigDialogViewModel.RoiWidth)
            or nameof(OcrScannerConfigDialogViewModel.RoiHeight))
        {
            if (!_isDraggingRoi)
            {
                UpdateRoiRectangleVisual();
            }
        }
    }

    private void UpdatePreviewImage()
    {
        var bytesToDisplay = ViewModel.ShowPreprocessedPreview && ViewModel.PreprocessedPreviewBytes is { Length: > 0 }
            ? ViewModel.PreprocessedPreviewBytes
            : ViewModel.OriginalSampleBytes ?? ViewModel.PreprocessedPreviewBytes;

        if (bytesToDisplay is not { Length: > 0 })
        {
            SamplePreviewImage.Source = null;
            EmptyPreviewPlaceholder.Visibility = Visibility.Visible;
            RoiSelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytesToDisplay);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            SamplePreviewImage.Source = bitmap;
            EmptyPreviewPlaceholder.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(new Action(UpdateRoiRectangleVisual));
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogWarning($"[OcrScannerConfigDialog] No se pudo decodificar la vista previa de imagen: {ex.Message}");
            SamplePreviewImage.Source = null;
            EmptyPreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private Rect GetRenderedImageBounds()
    {
        if (SamplePreviewImage.Source is not BitmapSource bitmap
            || bitmap.PixelWidth <= 0
            || bitmap.PixelHeight <= 0
            || PreviewContainerGrid.ActualWidth <= 0
            || PreviewContainerGrid.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var containerW = PreviewContainerGrid.ActualWidth;
        var containerH = PreviewContainerGrid.ActualHeight;
        var scale = Math.Min(containerW / bitmap.PixelWidth, containerH / bitmap.PixelHeight);
        var renderedW = bitmap.PixelWidth * scale;
        var renderedH = bitmap.PixelHeight * scale;
        var offsetX = (containerW - renderedW) / 2.0;
        var offsetY = (containerH - renderedH) / 2.0;

        return new Rect(offsetX, offsetY, renderedW, renderedH);
    }

    private void UpdateRoiRectangleVisual()
    {
        var imageBounds = GetRenderedImageBounds();
        if (imageBounds.IsEmpty || !ViewModel.HasCustomRoi || ViewModel.ShowPreprocessedPreview)
        {
            RoiSelectionRect.Visibility = Visibility.Collapsed;
            return;
        }

        var left = imageBounds.Left + (ViewModel.RoiX * imageBounds.Width);
        var top = imageBounds.Top + (ViewModel.RoiY * imageBounds.Height);
        var width = Math.Max(4.0, ViewModel.RoiWidth * imageBounds.Width);
        var height = Math.Max(4.0, ViewModel.RoiHeight * imageBounds.Height);

        Canvas.SetLeft(RoiSelectionRect, left);
        Canvas.SetTop(RoiSelectionRect, top);
        RoiSelectionRect.Width = width;
        RoiSelectionRect.Height = height;
        RoiSelectionRect.Visibility = Visibility.Visible;
    }

    private void PreviewContainerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRoiRectangleVisual();
    }

    private void RoiOverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.ShowPreprocessedPreview)
        {
            return;
        }

        var imageBounds = GetRenderedImageBounds();
        if (imageBounds.IsEmpty)
        {
            return;
        }

        var pos = e.GetPosition(RoiOverlayCanvas);
        var clampedX = Math.Clamp(pos.X, imageBounds.Left, imageBounds.Right);
        var clampedY = Math.Clamp(pos.Y, imageBounds.Top, imageBounds.Bottom);

        _dragStartPoint = new Point(clampedX, clampedY);
        _isDraggingRoi = true;
        RoiOverlayCanvas.CaptureMouse();

        Canvas.SetLeft(RoiSelectionRect, clampedX);
        Canvas.SetTop(RoiSelectionRect, clampedY);
        RoiSelectionRect.Width = 1;
        RoiSelectionRect.Height = 1;
        RoiSelectionRect.Visibility = Visibility.Visible;
    }

    private void RoiOverlayCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingRoi || _dragStartPoint is not Point start)
        {
            return;
        }

        var imageBounds = GetRenderedImageBounds();
        if (imageBounds.IsEmpty)
        {
            return;
        }

        var current = e.GetPosition(RoiOverlayCanvas);
        var clampedX = Math.Clamp(current.X, imageBounds.Left, imageBounds.Right);
        var clampedY = Math.Clamp(current.Y, imageBounds.Top, imageBounds.Bottom);

        var left = Math.Min(start.X, clampedX);
        var top = Math.Min(start.Y, clampedY);
        var width = Math.Max(4.0, Math.Abs(clampedX - start.X));
        var height = Math.Max(4.0, Math.Abs(clampedY - start.Y));

        Canvas.SetLeft(RoiSelectionRect, left);
        Canvas.SetTop(RoiSelectionRect, top);
        RoiSelectionRect.Width = width;
        RoiSelectionRect.Height = height;
    }

    private void RoiOverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingRoi || _dragStartPoint is not Point start)
        {
            return;
        }

        _isDraggingRoi = false;
        _dragStartPoint = null;
        RoiOverlayCanvas.ReleaseMouseCapture();

        var imageBounds = GetRenderedImageBounds();
        if (imageBounds.IsEmpty || imageBounds.Width <= 1 || imageBounds.Height <= 1)
        {
            return;
        }

        var end = e.GetPosition(RoiOverlayCanvas);
        var clampedX = Math.Clamp(end.X, imageBounds.Left, imageBounds.Right);
        var clampedY = Math.Clamp(end.Y, imageBounds.Top, imageBounds.Bottom);

        var left = Math.Min(start.X, clampedX);
        var top = Math.Min(start.Y, clampedY);
        var width = Math.Abs(clampedX - start.X);
        var height = Math.Abs(clampedY - start.Y);

        if (width < 10.0 || height < 10.0)
        {
            UpdateRoiRectangleVisual();
            return;
        }

        var normX = (left - imageBounds.Left) / imageBounds.Width;
        var normY = (top - imageBounds.Top) / imageBounds.Height;
        var normW = width / imageBounds.Width;
        var normH = height / imageBounds.Height;

        ViewModel.SetNormalizedRoi(normX, normY, normW, normH);
        UpdateRoiRectangleVisual();
    }

    private async void ApplyToInvoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ApplyToInvoiceCommand.CanExecute(null))
        {
            await ViewModel.ApplyToInvoiceCommand.ExecuteAsync(null);
            if (ViewModel.ApplyResultToStaging)
            {
                DialogResult = true;
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = ViewModel.SavedTemplate is not null;
    }
}
