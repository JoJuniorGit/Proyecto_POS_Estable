using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
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

    private string? _lastScannedOcrFilePath;

    [ObservableProperty]
    private bool _isOcrSource;

    [ObservableProperty]
    private string? _ocrDetectedRif;

    [ObservableProperty]
    private string? _ocrDetectedSupplierName;

    /// <summary>8.160-S2: número de factura best-effort detectado por el OCR; null si no aparece.</summary>
    [ObservableProperty]
    private string? _ocrDetectedInvoiceNumber;

    /// <summary>8.172: tasa de cambio best-effort detectada por el OCR; null si no aparece o es &lt;= 0.</summary>
    [ObservableProperty]
    private decimal? _ocrDetectedExchangeRate;

    public bool CanScanInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null;
    public bool CanConfigureOcrScanner => CanMutateCatalog && !IsBusy && SelectedSupplier is not null;

    /// <summary>8.147-S5/S6: el origen OCR propaga el editor a las líneas ya cargadas sin deshabilitar productos inexistentes.</summary>
    partial void OnIsOcrSourceChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSelectedFilePath));
        foreach (var line in Lines)
        {
            line.IsEditorEnabled = value || line.CanCreateProduct || line.WasUnresolved;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureOcrScanner))]
    private async Task ConfigureOcrScannerAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (SelectedSupplier is null)
        {
            _dialogService.ShowWarning("Datos incompletos", "Seleccione un proveedor antes de configurar el escáner OCR.");
            return;
        }

        SupplierOcrTemplateDto? initialTemplate = SelectedSupplier.OcrTemplate;
        try
        {
            var fetched = await _invoiceService.GetSupplierOcrTemplateAsync(SelectedSupplier.Id, cancellationToken);
            if (fetched is not null)
            {
                initialTemplate = fetched;
            }
        }
        catch
        {
            // Usa la plantilla en memoria si la consulta previa no estuvo disponible.
        }

        var dialogVm = new OcrScannerConfigDialogViewModel(
            SelectedSupplier.Id,
            SelectedSupplier.CommercialName,
            SelectedSupplier.ColumnMapping,
            initialTemplate,
            _invoiceService,
            _filePicker,
            _lastScannedOcrFilePath);

        _dialogService.ShowOcrScannerConfigDialog(dialogVm);

        if (dialogVm.SavedTemplate is not null && SelectedSupplier is not null)
        {
            var updatedSupplier = SelectedSupplier with { OcrTemplate = dialogVm.SavedTemplate };
            var index = Suppliers.IndexOf(SelectedSupplier);
            if (index >= 0)
            {
                Suppliers[index] = updatedSupplier;
            }

            SelectedSupplier = updatedSupplier;
            StatusMessage = $"Plantilla OCR actualizada para {updatedSupplier.CommercialName}.";
        }

        if (dialogVm.ApplyResultToStaging && dialogVm.LastExtractionResult is not null)
        {
            IsBusy = true;
            ClearOcrReviewState();
            try
            {
                await ProcessOcrExtractionResultAsync(dialogVm.LastExtractionResult, cancellationToken);
            }
            catch (Exception ex)
            {
                StatusMessage = $"No se pudo aplicar el resultado OCR: {ex.Message}";
                _dialogService.ShowError("Error al aplicar OCR", StatusMessage);
            }
            finally
            {
                IsBusy = false;
                RefreshCommandStates();
            }
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

        _lastScannedOcrFilePath = filePath;
        IsBusy = true;
        ClearOcrReviewState();
        StatusMessage = "Extrayendo el texto de la factura escaneada...";
        try
        {
            var result = SelectedSupplier.OcrTemplate is not null
                ? await _invoiceService.ExtractOcrAsync(
                    filePath,
                    SelectedSupplier.Id,
                    SelectedSupplier.ColumnMapping,
                    SelectedSupplier.OcrTemplate,
                    cancellationToken)
                : await _invoiceService.ExtractOcrAsync(
                    filePath,
                    SelectedSupplier.Id,
                    SelectedSupplier.ColumnMapping,
                    cancellationToken);

            await ProcessOcrExtractionResultAsync(result, cancellationToken);
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

    private async Task ProcessOcrExtractionResultAsync(
        OcrExtractionResultDto result,
        CancellationToken cancellationToken)
    {
        var lines = BuildOcrStageLines(result.Lines);

        // 8.147-L8: bloquear en vez de stagear filas que el backend descartaría sin aviso.
        if (lines.Any(line => !HasReadableIdentifier(line)))
        {
            StatusMessage = OcrMissingIdentifierMessage;
            _dialogService.ShowError("Filas OCR incompletas", OcrMissingIdentifierMessage);
            return;
        }

        // 8.174: se registra ANTES de aplicar el estado OCR para poder avisar en el status que la
        // moneda del documento se ajustó sola por la tasa impresa detectada.
        var currencyAdjustedForDetectedRate = result.DetectedExchangeRate is > 0m && !IsBsSCurrency;
        ApplyOcrReviewState(result);

        if (lines.Count == 0)
        {
            StatusMessage = "El OCR no detectó líneas legibles en la factura. Verifique el enfoque y la iluminación de la foto e intente de nuevo.";
            _dialogService.ShowWarning("Sin líneas OCR", StatusMessage);
            return;
        }

        await StagePreparedLinesAsync(lines, columnMapping: null, ocrSourced: true, cancellationToken);

        if (currencyAdjustedForDetectedRate)
        {
            StatusMessage += " Moneda ajustada a Bs.S por la tasa detectada en la factura.";
        }
    }

    private static IReadOnlyList<StageLineDto> BuildOcrStageLines(IReadOnlyList<OcrExtractedLineDto> lines) =>
        lines.Select(line => new StageLineDto(
            line.SupplierCode,
            line.Barcode,
            OcrConfidence.SanitizeProductName(line.Name),
            line.Quantity ?? 0m,
            line.UnitCost ?? 0m,
            line.NameConfidence,
            line.QuantityConfidence,
            line.UnitCostConfidence,
            line.ExtractedConversionFactor)).ToArray();

    private static bool HasReadableIdentifier(StageLineDto line) =>
        !string.IsNullOrWhiteSpace(line.SupplierCode) ||
        !string.IsNullOrWhiteSpace(line.Barcode) ||
        !string.IsNullOrWhiteSpace(line.Name);

    private void ApplyOcrReviewState(OcrExtractionResultDto result)
    {
        OcrDetectedRif = NormalizeOcrHint(result.DetectedRif);
        OcrDetectedSupplierName = NormalizeOcrHint(result.DetectedSupplierName);
        OcrDetectedInvoiceNumber = NormalizeOcrHint(result.DetectedInvoiceNumber);
        OcrDetectedExchangeRate = result.DetectedExchangeRate is decimal rate && rate > 0m
            ? Math.Round(rate, 4, MidpointRounding.AwayFromZero)
            : null;
        IsOcrSource = true;

        // 8.174: la tasa impresa en el documento es evidencia de una factura en Bs.S. Si el selector
        // aún no está en Bs.S (proveedor sin moneda recordada o primera factura), se ajusta para que
        // la tasa quede aplicada y editable al terminar el escaneo; toda corrección posterior de la
        // tasa recalcula los montos de la grilla en vivo (PropagateCurrencyAndRateToLines).
        if (OcrDetectedExchangeRate is decimal detectedRate)
        {
            // 8.178: una nueva detección OCR re-basa la tasa del documento; una corrección manual
            // previa deja de protegerse (el escaneo más reciente es la referencia).
            _isRateManuallyEdited = false;

            if (!IsBsSCurrency)
            {
                SelectedCurrency = CurrencyCodes.BsS;
            }

            SetAppliedRateTextProgrammatically(FormatAppliedRateText(detectedRate));
        }

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
        OcrDetectedExchangeRate = null;
        _isRateManuallyEdited = false; // 8.178
    }

    private static string? NormalizeOcrHint(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
