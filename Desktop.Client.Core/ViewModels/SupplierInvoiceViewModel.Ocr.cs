using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class SupplierInvoiceViewModel
{
    /// <summary>
    /// 8.147-S6/L8: el staging backend descarta las filas sin código, barras ni nombre
    /// (<c>IsReadableLine</c>). El flujo OCR bloquea el envío cuando alguna fila no tiene
    /// identificador legible para que no desaparezca en silencio.
    /// </summary>
    public const string OcrMissingIdentifierMessage =
        "Hay filas OCR sin nombre ni código; complete al menos un identificador antes de cargar.";

    public const string OcrFileFilter =
        "Facturas escaneadas (*.png;*.jpg;*.jpeg;*.pdf)|*.png;*.jpg;*.jpeg;*.pdf|" +
        "Imágenes (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|PDF (*.pdf)|*.pdf";

    public const double MinOcrZoom = 0.5;
    public const double MaxOcrZoom = 4.0;
    private const double OcrZoomStep = 0.25;

    /// <summary>Previews PNG por página (base64) tal como las devuelve el backend; sin tipos WPF.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _ocrPreviewPagesBase64 = [];

    [ObservableProperty]
    private int _ocrSelectedPageIndex;

    [ObservableProperty]
    private double _ocrZoom = 1.0;

    [ObservableProperty]
    private bool _isOcrSource;

    [ObservableProperty]
    private string? _ocrDetectedRif;

    [ObservableProperty]
    private string? _ocrDetectedSupplierName;

    public bool HasOcrPreviews => OcrPreviewPagesBase64.Count > 0;
    public bool CanNavigateOcrNextPage =>
        OcrPreviewPagesBase64.Count > 0 && OcrSelectedPageIndex < OcrPreviewPagesBase64.Count - 1;
    public bool CanNavigateOcrPreviousPage => OcrSelectedPageIndex > 0;

    /// <summary>
    /// 8.147-S5: base64 de la página seleccionada. Propiedad calculada (en vez de un indexer en
    /// XAML) para garantizar la notificación al cambiar de página y alimentar al converter.
    /// </summary>
    public string? OcrSelectedPreviewBase64 => OcrPreviewPagesBase64.Count == 0
        ? null
        : OcrPreviewPagesBase64[Math.Clamp(OcrSelectedPageIndex, 0, OcrPreviewPagesBase64.Count - 1)];

    /// <summary>8.147-S5: etiqueta "Página X/Y" del panel de previews; vacía sin páginas.</summary>
    public string OcrPageLabel => OcrPreviewPagesBase64.Count == 0
        ? string.Empty
        : $"Página {OcrSelectedPageIndex + 1}/{OcrPreviewPagesBase64.Count}";

    public bool CanScanInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null;

    /// <summary>8.147-S5/S6: el origen OCR propaga el editor a las líneas ya cargadas.</summary>
    partial void OnIsOcrSourceChanged(bool value)
    {
        foreach (var line in Lines)
        {
            line.IsEditorEnabled = value;
        }
    }

    partial void OnOcrPreviewPagesBase64Changed(IReadOnlyList<string> value)
    {
        var maxIndex = Math.Max(0, value.Count - 1);
        if (OcrSelectedPageIndex > maxIndex)
        {
            OcrSelectedPageIndex = maxIndex;
        }

        OnPropertyChanged(nameof(HasOcrPreviews));
        NotifyOcrNavigationChanged();
    }

    partial void OnOcrSelectedPageIndexChanged(int value)
    {
        var maxIndex = Math.Max(0, OcrPreviewPagesBase64.Count - 1);
        var clamped = Math.Clamp(value, 0, maxIndex);
        if (clamped != value)
        {
            OcrSelectedPageIndex = clamped;
            return;
        }

        NotifyOcrNavigationChanged();
    }

    partial void OnOcrZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MinOcrZoom, MaxOcrZoom);
        if (clamped != value)
        {
            OcrZoom = clamped;
        }
    }

    [RelayCommand(CanExecute = nameof(CanScanInvoice))]
    private async Task ScanInvoiceAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (SelectedSupplier is null)
        {
            _dialogService.ShowWarning("Datos incompletos", "Seleccione un proveedor antes de escanear la factura.");
            return;
        }

        var filePath = _filePicker.PickFilePath("Escanear factura (OCR)", OcrFileFilter);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        await ScanOcrFileAsync(filePath, cancellationToken);
    }

    /// <summary>
    /// 8.147-S4/L4 (T7): captura por cámara opcional. Si el diálogo devuelve una ruta (JPG
    /// temporal) entra al mismo camino de extracción que el archivo; null (cancelado o sin
    /// cámara) no hace nada y deja el camino de archivo intacto.
    /// </summary>
    [RelayCommand]
    private async Task ScanCameraAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (SelectedSupplier is null)
        {
            _dialogService.ShowWarning("Datos incompletos", "Seleccione un proveedor antes de escanear la factura.");
            return;
        }

        var capturedFilePath = _dialogService.ShowCameraCaptureDialog();
        if (string.IsNullOrWhiteSpace(capturedFilePath))
        {
            return;
        }

        await ScanOcrFileAsync(capturedFilePath, cancellationToken);
    }

    /// <summary>
    /// Extrae las líneas del archivo escaneado y las stagea como origen OCR. Punto de entrada
    /// compartido con la futura captura por cámara (T7).
    /// </summary>
    public async Task ScanOcrFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (SelectedSupplier is null)
        {
            _dialogService.ShowWarning("Datos incompletos", "Seleccione un proveedor antes de escanear la factura.");
            return;
        }

        IsBusy = true;
        ClearOcrReviewState();
        StatusMessage = "Extrayendo el texto de la factura escaneada...";
        try
        {
            var result = await _invoiceService.ExtractOcrAsync(
                filePath,
                SelectedSupplier.Id,
                SelectedSupplier.ColumnMapping,
                cancellationToken);

            var lines = BuildOcrStageLines(result.Lines);

            // 8.147-L8: bloquear en vez de stagear filas que el backend descartaría sin aviso.
            if (lines.Any(line => !HasReadableIdentifier(line)))
            {
                StatusMessage = OcrMissingIdentifierMessage;
                _dialogService.ShowError("Filas OCR incompletas", OcrMissingIdentifierMessage);
                return;
            }

            ApplyOcrReviewState(result);

            if (lines.Count == 0)
            {
                StatusMessage = "El OCR no detectó líneas legibles en la factura.";
                _dialogService.ShowWarning("Sin líneas OCR", StatusMessage);
                return;
            }

            await StagePreparedLinesAsync(lines, columnMapping: null, ocrSourced: true, cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo leer la factura escaneada: {exception.Message}";
            _dialogService.ShowError("Error al escanear la factura", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand]
    private void ResetOcrZoom() => OcrZoom = 1.0;

    [RelayCommand]
    private void OcrZoomIn() => OcrZoom += OcrZoomStep;

    [RelayCommand]
    private void OcrZoomOut() => OcrZoom -= OcrZoomStep;

    [RelayCommand(CanExecute = nameof(CanNavigateOcrPreviousPage))]
    private void OcrPreviousPage()
    {
        if (CanNavigateOcrPreviousPage)
        {
            OcrSelectedPageIndex--;
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateOcrNextPage))]
    private void OcrNextPage()
    {
        if (CanNavigateOcrNextPage)
        {
            OcrSelectedPageIndex++;
        }
    }

    private static IReadOnlyList<StageLineDto> BuildOcrStageLines(IReadOnlyList<OcrExtractedLineDto> lines) =>
        lines.Select(line => new StageLineDto(
            line.SupplierCode,
            line.Barcode,
            line.Name,
            line.Quantity ?? 0m,
            line.UnitCost ?? 0m,
            line.NameConfidence,
            line.QuantityConfidence,
            line.UnitCostConfidence)).ToArray();

    private static bool HasReadableIdentifier(StageLineDto line) =>
        !string.IsNullOrWhiteSpace(line.SupplierCode) ||
        !string.IsNullOrWhiteSpace(line.Barcode) ||
        !string.IsNullOrWhiteSpace(line.Name);

    private void ApplyOcrReviewState(OcrExtractionResultDto result)
    {
        OcrPreviewPagesBase64 = result.PreviewPagesBase64 ?? [];
        OcrDetectedRif = NormalizeOcrHint(result.DetectedRif);
        OcrDetectedSupplierName = NormalizeOcrHint(result.DetectedSupplierName);
        IsOcrSource = true;

        // Sugerencia best-effort: nunca pisa un valor ya escrito ni selecciona proveedor.
        if (OcrDetectedRif is not null && string.IsNullOrWhiteSpace(SupplierRifOrNit))
        {
            SupplierRifOrNit = OcrDetectedRif;
        }

        if (OcrDetectedSupplierName is not null && string.IsNullOrWhiteSpace(SupplierCommercialName))
        {
            SupplierCommercialName = OcrDetectedSupplierName;
        }
    }

    private void ClearOcrReviewState()
    {
        OcrPreviewPagesBase64 = [];
        OcrSelectedPageIndex = 0;
        OcrZoom = 1.0;
        IsOcrSource = false;
        OcrDetectedRif = null;
        OcrDetectedSupplierName = null;
    }

    private void NotifyOcrNavigationChanged()
    {
        OnPropertyChanged(nameof(CanNavigateOcrNextPage));
        OnPropertyChanged(nameof(CanNavigateOcrPreviousPage));
        OnPropertyChanged(nameof(OcrPageLabel));
        OnPropertyChanged(nameof(OcrSelectedPreviewBase64));
        OcrNextPageCommand.NotifyCanExecuteChanged();
        OcrPreviousPageCommand.NotifyCanExecuteChanged();
    }

    private static string? NormalizeOcrHint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
