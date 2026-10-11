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
        "Facturas escaneadas (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.pdf)|" +
        "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.pdf|" +
        "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff)|" +
        "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|PDF (*.pdf)|*.pdf";

    [ObservableProperty]
    private bool _isOcrSource;

    [ObservableProperty]
    private string? _ocrDetectedRif;

    [ObservableProperty]
    private string? _ocrDetectedSupplierName;

    /// <summary>8.160-S2: número de factura best-effort detectado por el OCR; null si no aparece.</summary>
    [ObservableProperty]
    private string? _ocrDetectedInvoiceNumber;

    public bool CanScanInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null;

    /// <summary>8.147-S5/S6: el origen OCR propaga el editor a las líneas ya cargadas.</summary>
    partial void OnIsOcrSourceChanged(bool value)
    {
        foreach (var line in Lines)
        {
            line.IsEditorEnabled = value;
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
    /// Extrae las líneas del archivo escaneado y las stagea como origen OCR. Único punto de
    /// entrada del flujo de captura por archivo (L4: la carga de imagen no depende de cámara).
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
        OcrDetectedRif = NormalizeOcrHint(result.DetectedRif);
        OcrDetectedSupplierName = NormalizeOcrHint(result.DetectedSupplierName);
        OcrDetectedInvoiceNumber = NormalizeOcrHint(result.DetectedInvoiceNumber);
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
        IsOcrSource = false;
        OcrDetectedRif = null;
        OcrDetectedSupplierName = null;
        OcrDetectedInvoiceNumber = null;
    }

    private static string? NormalizeOcrHint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
