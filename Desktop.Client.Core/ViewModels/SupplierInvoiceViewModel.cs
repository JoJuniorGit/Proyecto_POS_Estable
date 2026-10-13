using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Interfaces;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClientSupplierInvoiceService = Desktop.Client.Services.ISupplierInvoiceService;

namespace Desktop.Client.ViewModels;

public partial class SupplierInvoiceViewModel : ObservableObject, IDisposable
{
    private readonly ClientSupplierInvoiceService _invoiceService;
    private readonly IDialogService _dialogService;
    private readonly IFilePickerDialog _filePicker;
    private readonly IExchangeRateService _exchangeRateService;
    private int _disposed;
    private bool _isCreatingProduct;
    private bool _isUpdatingBulkApproval;
    private bool _isPropagatingLinkedGroup;

    public UserSession UserSession { get; }
    public bool CanMutateCatalog => UserSession.CanMutateCatalog;
    public ObservableCollection<SupplierInvoiceColumnMappingItem> ColumnMappings { get; } = new();
    public ObservableCollection<SupplierInvoiceLineViewModel> Lines { get; } = new();
    public IReadOnlyList<string> CurrencyOptions { get; } = new[] { CurrencyCodes.Usd, CurrencyCodes.BsS };

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Seleccione un archivo de factura y un proveedor para comenzar.";

    [ObservableProperty]
    private string _selectedFilePath = string.Empty;

    [ObservableProperty]
    private string _supplierRifOrNit = string.Empty;

    [ObservableProperty]
    private string _supplierCommercialName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SupplierSummaryDto> _suppliers = new();

    [ObservableProperty]
    private SupplierSummaryDto? _selectedSupplier;

    [ObservableProperty]
    private SupplierInvoiceDetailDto? _stagedInvoice;

    [ObservableProperty]
    private string _selectedCurrency = CurrencyCodes.Usd;

    [ObservableProperty]
    private string _appliedRateText = string.Empty;

    public bool IsBsSCurrency => string.Equals(SelectedCurrency, CurrencyCodes.BsS, StringComparison.OrdinalIgnoreCase);
    public bool IsBsSInvoice => string.Equals(StagedInvoice?.Currency, CurrencyCodes.BsS, StringComparison.OrdinalIgnoreCase);

    /// <summary>8.164: encabezado del costo de factura editable según la moneda del documento.</summary>
    public string DocumentCostHeader => IsBsSInvoice ? "Costo factura (Bs.S)" : "Costo factura (USD)";

    /// <summary>8.168: oculta el campo de ruta de archivo cuando está vacío o en flujo OCR.</summary>
    public bool HasSelectedFilePath => !string.IsNullOrWhiteSpace(SelectedFilePath) && !IsOcrSource;

    /// <summary>8.168: indica si existen líneas resueltas que pueden aprobarse masivamente.</summary>
    public bool HasApprovableLines => Lines.Any(line => line.CanApprove);

    /// <summary>
    /// 8.168: estado tri-estado del selector maestro "Aprobar todas" en el encabezado de la columna
    /// (true = todas las aprobables marcadas, false = ninguna o sin aprobables, null = selección parcial).
    /// </summary>
    public bool? AreAllApprovableLinesApproved
    {
        get
        {
            var approvableCount = 0;
            var approvedCount = 0;
            foreach (var line in Lines)
            {
                if (!line.CanApprove)
                {
                    continue;
                }

                approvableCount++;
                if (line.IsApproved)
                {
                    approvedCount++;
                }
            }

            if (approvableCount == 0 || approvedCount == 0)
            {
                return false;
            }

            return approvedCount == approvableCount ? true : null;
        }
        set
        {
            var shouldApprove = value == true;
            _isUpdatingBulkApproval = true;
            try
            {
                foreach (var line in Lines)
                {
                    if (line.CanApprove)
                    {
                        line.IsApproved = shouldApprove;
                    }
                }
            }
            finally
            {
                _isUpdatingBulkApproval = false;
            }

            OnPropertyChanged(nameof(AreAllApprovableLinesApproved));
            OnPropertyChanged(nameof(CanConfirmInvoice));
            ConfirmInvoiceCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Factura Bs.S exige una tasa aplicada válida (&gt; 0); en USD la tasa se normaliza a 1.</summary>
    public bool HasValidAppliedRate => !IsBsSCurrency || (TryParseAppliedRate(AppliedRateText, out var rate) && rate > 0m);

    public bool ShowRateValidationHint => IsBsSCurrency && !HasValidAppliedRate;
    public bool IsCurrencyInputEnabled => CanMutateCatalog && !IsBusy;
    public bool IsRateInputEnabled => IsCurrencyInputEnabled && IsBsSCurrency;

    public bool CanSelectFile => CanMutateCatalog && !IsBusy;

    /// <summary>8.161: el alta de proveedor vive en un modal; el boton solo exige permiso y no estar ocupado.</summary>
    public bool CanOpenRegisterSupplier => CanMutateCatalog && !IsBusy;

    public bool CanStageInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null &&
        !string.IsNullOrWhiteSpace(SelectedFilePath) && HasFileHeaders && HasValidColumnMapping() &&
        HasValidAppliedRate;
    public bool CanConfirmInvoice => CanMutateCatalog && !IsBusy && StagedInvoice is not null &&
        HasValidAppliedRate &&
        Lines.Any(line => line.IsApproved && line.CanApprove);

    public SupplierInvoiceViewModel(
        ClientSupplierInvoiceService invoiceService,
        UserSession userSession,
        IDialogService dialogService,
        IFilePickerDialog filePicker,
        IExchangeRateService exchangeRateService)
    {
        ArgumentNullException.ThrowIfNull(invoiceService);
        ArgumentNullException.ThrowIfNull(userSession);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(exchangeRateService);

        _invoiceService = invoiceService;
        UserSession = userSession;
        _dialogService = dialogService;
        _filePicker = filePicker;
        _exchangeRateService = exchangeRateService;
        UserSession.PropertyChanged += OnUserSessionPropertyChanged;
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    partial void OnSelectedFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasSelectedFilePath));
        RefreshCommandStates();
    }

    partial void OnStagedInvoiceChanged(SupplierInvoiceDetailDto? value)
    {
        OnPropertyChanged(nameof(CanConfirmInvoice));
        OnPropertyChanged(nameof(IsBsSInvoice));
        OnPropertyChanged(nameof(DocumentCostHeader));
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectFile))]
    private async Task SelectFileAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        var filePath = _filePicker.PickFilePath(
            "Seleccionar factura de proveedor",
            "Facturas compatibles (*.xlsx;*.csv;*.xml)|*.xlsx;*.csv;*.xml|Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|XML (*.xml)|*.xml");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            await LoadFileAsync(filePath, cancellationToken);
        }
    }

    public async Task LoadFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        IsBusy = true;
        SelectedFilePath = filePath;
        ClearOcrReviewState();
        StagedInvoice = null;
        ClearLines();
        StatusMessage = "Leyendo encabezados del archivo...";

        try
        {
            var headers = await _invoiceService.ReadHeadersAsync(filePath, cancellationToken);
            if (headers.Count == 0)
            {
                throw new InvalidOperationException("El archivo no contiene encabezados legibles.");
            }

            StatusMessage = "Revise la asignación de columnas antes de preparar la factura.";
            SetFileHeaders(headers);
        }
        catch (Exception exception)
        {
            SetFileHeaders([]);
            StatusMessage = $"No se pudo leer el archivo: {exception.Message}";
            _dialogService.ShowError("Error de lectura", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStageInvoice))]
    private async Task StageInvoiceAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (!CanStageInvoice || SelectedSupplier is null || string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Leyendo y preparando las líneas para revisión...";
        try
        {
            var columnMapping = BuildColumnMapping();
            var lines = await _invoiceService.ParseFileWithMappingAsync(
                SelectedFilePath,
                columnMapping,
                cancellationToken);
            await StagePreparedLinesAsync(lines, columnMapping, ocrSourced: false, cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo preparar la factura: {exception.Message}";
            _dialogService.ShowError("Error al preparar la factura", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// Núcleo de staging compartido por la ruta tabular (mapping + OcrSourced false) y la ruta
    /// OCR (mapping null + OcrSourced true, D11). El cache local de plantilla solo se actualiza
    /// cuando hay mapping; la extracción OCR no debe borrar la plantilla guardada.
    /// </summary>
    private async Task StagePreparedLinesAsync(
        IReadOnlyList<StageLineDto> lines,
        SupplierColumnMappingDto? columnMapping,
        bool ocrSourced,
        CancellationToken cancellationToken)
    {
        var supplier = SelectedSupplier
            ?? throw new InvalidOperationException("Seleccione un proveedor antes de preparar la factura.");
        var appliedRate = IsBsSCurrency
            ? (TryParseAppliedRate(AppliedRateText, out var parsedRate) ? parsedRate : 0m)
            : 1m;
        var request = new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            columnMapping,
            lines,
            SelectedCurrency,
            appliedRate,
            ocrSourced);
        var invoice = await _invoiceService.StageAsync(request, cancellationToken);

        if (columnMapping is not null)
        {
            var updatedSupplier = supplier with { ColumnMapping = columnMapping };
            var supplierIndex = Suppliers.IndexOf(supplier);
            if (supplierIndex >= 0)
            {
                Suppliers[supplierIndex] = updatedSupplier;
            }

            SelectedSupplier = updatedSupplier;
        }

        LoadStagedInvoice(invoice);
        StatusMessage = $"Factura #{invoice.Id} preparada. Revise las líneas antes de confirmar.";
    }

    [RelayCommand(CanExecute = nameof(CanConfirmInvoice))]
    public async Task ConfirmInvoiceAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (!CanConfirmInvoice || StagedInvoice is null)
        {
            return;
        }

        if (Lines.Any(line => line.CanCreateProduct && line.IsApproved))
        {
            StatusMessage = "Resuelva (vincule o cree) los productos nuevos marcados con Aprobar, o desmarque su casilla Aprobar para omitirlos.";
            _dialogService.ShowWarning(
                "Productos nuevos sin resolver",
                StatusMessage);
            return;
        }

        var approvedLines = Lines
            .Where(line => line.IsApproved && line.CanApprove)
            .ToArray();
        if (approvedLines.Length == 0)
        {
            _dialogService.ShowWarning("Sin líneas aprobadas", "Apruebe al menos una línea válida antes de procesar la factura.");
            return;
        }


        if (!_dialogService.ShowConfirm(
                "Confirmar factura",
                $"¿Confirma el procesamiento de {approvedLines.Length} línea(s) aprobada(s)? Esta acción actualizará costos, márgenes y existencias."))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Procesando la factura aprobada...";
        try
        {
            decimal? confirmAppliedRate = IsBsSInvoice
                && TryParseAppliedRate(AppliedRateText, out var parsedConfirmRate)
                && parsedConfirmRate > 0m
                ? Math.Round(parsedConfirmRate, 4, MidpointRounding.AwayFromZero)
                : null;
            var request = new ConfirmSupplierInvoiceRequestDto(
                approvedLines.Select(line => line.ToConfirmLine()).ToArray(),
                confirmAppliedRate);
            var result = await _invoiceService.ConfirmAsync(StagedInvoice.Id, request, cancellationToken);
            LoadStagedInvoice(result);
            ClearOcrReviewState();
            StatusMessage = $"Factura #{result.Id} procesada correctamente.";
            _dialogService.ShowInfo("Factura procesada", StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo procesar la factura: {exception.Message}";
            _dialogService.ShowError("Error al procesar la factura", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    public void LoadStagedInvoice(SupplierInvoiceDetailDto invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        StagedInvoice = invoice;
        ClearLines();
        if (IsBsSCurrency && string.IsNullOrWhiteSpace(AppliedRateText) && invoice.AppliedRate > 0m)
        {
            AppliedRateText = FormatAppliedRateText(invoice.AppliedRate);
        }

        var effectiveRate = (IsBsSCurrency || IsBsSInvoice)
            && TryParseAppliedRate(AppliedRateText, out var parsedRate)
            && parsedRate > 0m
            ? Math.Round(parsedRate, 4, MidpointRounding.AwayFromZero)
            : invoice.AppliedRate;
        foreach (var line in invoice.Lines)
        {
            var viewModel = new SupplierInvoiceLineViewModel(line, invoice.Currency, invoice.AppliedRate)
            {
                // 8.147-S5/S6 (T7): las líneas OCR y los productos inexistentes permiten editar sus campos.
                IsEditorEnabled = IsOcrSource || line.ResolvedProductId is null
            };
            if (effectiveRate != invoice.AppliedRate && effectiveRate > 0m)
            {
                viewModel.UpdateCurrencyAndRate(invoice.Currency, effectiveRate);
            }

            viewModel.PropertyChanged += OnLinePropertyChanged;
            Lines.Add(viewModel);
        }

        AssignExpandedLinkedGroups();

        OnPropertyChanged(nameof(HasApprovableLines));
        OnPropertyChanged(nameof(AreAllApprovableLinesApproved));
        OnPropertyChanged(nameof(CanConfirmInvoice));
        RefreshCommandStates();
    }

    private void AssignExpandedLinkedGroups()
    {
        for (var index = 0; index < Lines.Count; index++)
        {
            var candidate = Lines[index];
            if (candidate.ResolvedProductId is null
                || !string.Equals(candidate.MatchMethod, "SupplierCode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var groupEnd = index;
            while (groupEnd + 1 < Lines.Count)
            {
                var next = Lines[groupEnd + 1];
                if (next.ResolvedProductId is not null
                    && string.Equals(next.MatchMethod, "SupplierCode", StringComparison.OrdinalIgnoreCase)
                    && next.Quantity == 0m
                    && next.UnitCostDocument == candidate.UnitCostDocument
                    && string.Equals(next.SupplierCode, candidate.SupplierCode, StringComparison.OrdinalIgnoreCase))
                {
                    groupEnd++;
                    continue;
                }

                break;
            }

            if (groupEnd > index)
            {
                var groupKey = $"group-{candidate.LineId}";
                for (var memberIndex = index; memberIndex <= groupEnd; memberIndex++)
                {
                    Lines[memberIndex].LinkedGroupKey = groupKey;
                    Lines[memberIndex].IsEditorEnabled = true;
                }

                index = groupEnd;
            }
        }
    }

    [RelayCommand]
    private void ClearInvoice()
    {
        SelectedFilePath = string.Empty;
        StagedInvoice = null;
        ClearLines();
        SetFileHeaders([]);
        ClearOcrReviewState();
        StatusMessage = "Seleccione un archivo de factura y un proveedor para comenzar.";
        RefreshCommandStates();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        UserSession.PropertyChanged -= OnUserSessionPropertyChanged;
        ClearLines();
        foreach (var mapping in ColumnMappings)
        {
            mapping.PropertyChanged -= OnColumnMappingChanged;
        }

        ColumnMappings.Clear();
    }

    private void OnUserSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(UserSession.CurrentUser) or nameof(UserSession.CanMutateCatalog))
        {
            OnPropertyChanged(nameof(CanMutateCatalog));
            RefreshCommandStates();
        }
    }

    private void OnLinePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_isUpdatingBulkApproval)
        {
            return;
        }

        if (eventArgs.PropertyName == nameof(SupplierInvoiceLineViewModel.IsApproved))
        {
            OnPropertyChanged(nameof(AreAllApprovableLinesApproved));
            OnPropertyChanged(nameof(CanConfirmInvoice));
            ConfirmInvoiceCommand.NotifyCanExecuteChanged();
            return;
        }

        if (_isPropagatingLinkedGroup
            || sender is not SupplierInvoiceLineViewModel changedLine
            || string.IsNullOrWhiteSpace(changedLine.LinkedGroupKey))
        {
            return;
        }

        if (eventArgs.PropertyName is nameof(SupplierInvoiceLineViewModel.EditUnitCostDocument)
            or nameof(SupplierInvoiceLineViewModel.ConversionFactor))
        {
            _isPropagatingLinkedGroup = true;
            try
            {
                foreach (var sibling in Lines)
                {
                    if (ReferenceEquals(sibling, changedLine)
                        || !string.Equals(sibling.LinkedGroupKey, changedLine.LinkedGroupKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (eventArgs.PropertyName == nameof(SupplierInvoiceLineViewModel.EditUnitCostDocument))
                    {
                        sibling.EditUnitCostDocument = changedLine.EditUnitCostDocument;
                    }
                    else if (eventArgs.PropertyName == nameof(SupplierInvoiceLineViewModel.ConversionFactor))
                    {
                        sibling.ConversionFactor = changedLine.ConversionFactor;
                    }
                }
            }
            finally
            {
                _isPropagatingLinkedGroup = false;
            }
        }
    }

    private void ClearLines()
    {
        foreach (var line in Lines)
        {
            line.PropertyChanged -= OnLinePropertyChanged;
        }

        Lines.Clear();
        OnPropertyChanged(nameof(HasApprovableLines));
        OnPropertyChanged(nameof(AreAllApprovableLinesApproved));
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanSelectFile));
        OnPropertyChanged(nameof(CanOpenRegisterSupplier));
        OnPropertyChanged(nameof(CanStageInvoice));
        OnPropertyChanged(nameof(CanConfirmInvoice));
        OnPropertyChanged(nameof(CanScanInvoice));
        OnPropertyChanged(nameof(CanConfigureOcrScanner));
        OnPropertyChanged(nameof(IsCurrencyInputEnabled));
        OnPropertyChanged(nameof(IsRateInputEnabled));
        SelectFileCommand.NotifyCanExecuteChanged();
        OpenRegisterSupplierCommand.NotifyCanExecuteChanged();
        StageInvoiceCommand.NotifyCanExecuteChanged();
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();
        ScanInvoiceCommand.NotifyCanExecuteChanged();
        ConfigureOcrScannerCommand.NotifyCanExecuteChanged();
    }

    private void ShowAccessDenied() =>
        _dialogService.ShowWarning("Acceso denegado", "El rol actual no puede modificar el catálogo de productos.");

    private static string? NormalizeInput(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
