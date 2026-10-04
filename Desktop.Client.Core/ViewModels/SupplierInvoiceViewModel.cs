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

    /// <summary>Factura Bs.S exige una tasa aplicada válida (&gt; 0); en USD la tasa se normaliza a 1.</summary>
    public bool HasValidAppliedRate => !IsBsSCurrency || (TryParseAppliedRate(AppliedRateText, out var rate) && rate > 0m);

    public bool ShowRateValidationHint => IsBsSCurrency && !HasValidAppliedRate;
    public bool IsCurrencyInputEnabled => CanMutateCatalog && !IsBusy;
    public bool IsRateInputEnabled => IsCurrencyInputEnabled && IsBsSCurrency;

    public bool CanSelectFile => CanMutateCatalog && !IsBusy;
    public bool CanLookupSupplier => CanMutateCatalog && !IsBusy &&
        (!string.IsNullOrWhiteSpace(SupplierRifOrNit) || !string.IsNullOrWhiteSpace(SupplierCommercialName));
    public bool CanCreateSupplier => CanMutateCatalog && !IsBusy && SelectedSupplier is null &&
        !string.IsNullOrWhiteSpace(SupplierCommercialName);
    public bool CanStageInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null &&
        !string.IsNullOrWhiteSpace(SelectedFilePath) && HasFileHeaders && HasValidColumnMapping() &&
        HasValidAppliedRate;
    public bool CanConfirmInvoice => CanMutateCatalog && !IsBusy && StagedInvoice is not null &&
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

    partial void OnSelectedSupplierChanged(SupplierSummaryDto? value)
    {
        if (value is not null)
        {
            SupplierRifOrNit = value.RifOrNit ?? string.Empty;
            SupplierCommercialName = value.CommercialName;
        }

        ApplyCurrentColumnMapping();
        RefreshCommandStates();
    }

    partial void OnSupplierRifOrNitChanged(string value) => RefreshCommandStates();

    partial void OnSupplierCommercialNameChanged(string value) => RefreshCommandStates();

    partial void OnSelectedFilePathChanged(string value) => RefreshCommandStates();

    partial void OnSelectedCurrencyChanged(string value)
    {
        OnPropertyChanged(nameof(IsBsSCurrency));
        OnPropertyChanged(nameof(IsRateInputEnabled));
        OnPropertyChanged(nameof(HasValidAppliedRate));
        OnPropertyChanged(nameof(ShowRateValidationHint));
        OnPropertyChanged(nameof(CanStageInvoice));
        StageInvoiceCommand.NotifyCanExecuteChanged();

        if (IsBsSCurrency && string.IsNullOrWhiteSpace(AppliedRateText))
        {
            _ = PrefillAppliedRateAsync();
        }
    }

    partial void OnAppliedRateTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidAppliedRate));
        OnPropertyChanged(nameof(ShowRateValidationHint));
        OnPropertyChanged(nameof(CanStageInvoice));
        StageInvoiceCommand.NotifyCanExecuteChanged();
    }

    partial void OnStagedInvoiceChanged(SupplierInvoiceDetailDto? value)
    {
        OnPropertyChanged(nameof(CanConfirmInvoice));
        OnPropertyChanged(nameof(IsBsSInvoice));
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
        Lines.Clear();
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

    [RelayCommand(CanExecute = nameof(CanLookupSupplier))]
    private async Task LookupSupplierAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        IsBusy = true;
        try
        {
            var suppliers = await _invoiceService.GetSuppliersAsync(
                NormalizeInput(SupplierRifOrNit),
                NormalizeInput(SupplierCommercialName),
                cancellationToken);
            Suppliers = new ObservableCollection<SupplierSummaryDto>(suppliers);

            if (suppliers.Count == 0)
            {
                SelectedSupplier = null;
                StatusMessage = "No se encontró el proveedor. Complete el nombre para registrarlo.";
                _dialogService.ShowInfo("Proveedor no encontrado", StatusMessage);
            }
            else if (suppliers.Count > 1)
            {
                SelectedSupplier = null;
                StatusMessage = "Se encontraron varios proveedores. Seleccione el correcto antes de continuar.";
            }
            else
            {
                StatusMessage = "Proveedor seleccionado. Revise la asignación guardada de columnas.";
                SelectedSupplier = suppliers[0];
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo buscar el proveedor: {exception.Message}";
            _dialogService.ShowError("Error de búsqueda", StatusMessage);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateSupplier))]
    private async Task CreateSupplierAsync(CancellationToken cancellationToken)
    {
        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (string.IsNullOrWhiteSpace(SupplierCommercialName))
        {
            _dialogService.ShowWarning("Datos incompletos", "Ingrese el nombre comercial del proveedor.");
            return;
        }

        IsBusy = true;
        try
        {
            var supplier = await _invoiceService.CreateSupplierAsync(
                new CreateSupplierRequestDto(
                    NormalizeInput(SupplierRifOrNit),
                    SupplierCommercialName.Trim()),
                cancellationToken);
            Suppliers.Add(supplier);
            SelectedSupplier = supplier;
            StatusMessage = "Proveedor registrado y seleccionado.";
            _dialogService.ShowInfo("Proveedor registrado", StatusMessage);
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo registrar el proveedor: {exception.Message}";
            _dialogService.ShowError("Error de registro", StatusMessage);
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
            var request = new ConfirmSupplierInvoiceRequestDto(approvedLines
                .Select(line => line.ToConfirmLine())
                .ToArray());
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

    [RelayCommand]
    private async Task CreateProductAsync(SupplierInvoiceLineViewModel? line, CancellationToken cancellationToken)
    {
        if (line is null || StagedInvoice is null)
        {
            return;
        }

        if (!CanMutateCatalog)
        {
            ShowAccessDenied();
            return;
        }

        if (IsBusy || _isCreatingProduct || !line.CanCreateProduct)
        {
            return;
        }

        _isCreatingProduct = true;
        try
        {
            var dialogViewModel = new CreateInvoiceProductDialogViewModel(line, IsBsSInvoice);
            if (_dialogService.ShowCreateInvoiceProductDialog(dialogViewModel) != true)
            {
                return;
            }

            var barcode = dialogViewModel.Barcode.Trim();
            var productName = dialogViewModel.Name.Trim();
            IsBusy = true;
            StatusMessage = "Creando el producto en el catálogo...";
            try
            {
                var updatedInvoice = await _invoiceService.CreateProductFromLineAsync(
                    StagedInvoice.Id,
                    line.LineId,
                    new CreateInvoiceProductRequestDto(barcode, productName),
                    cancellationToken);
                LoadStagedInvoice(updatedInvoice);
                StatusMessage = $"Producto '{productName}' creado. Revise el costo y confirme la factura.";
            }
            catch (Exception exception)
            {
                StatusMessage = $"No se pudo crear el producto: {exception.Message}";
                _dialogService.ShowError("Error al crear el producto", StatusMessage);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _isCreatingProduct = false;
        }
    }

    public void LoadStagedInvoice(SupplierInvoiceDetailDto invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        StagedInvoice = invoice;
        ClearLines();
        foreach (var line in invoice.Lines)
        {
            var viewModel = new SupplierInvoiceLineViewModel(line)
            {
                // 8.147-S5/S6 (T7): solo las líneas de una factura OCR permiten revisar editando.
                IsEditorEnabled = IsOcrSource
            };
            viewModel.PropertyChanged += OnLinePropertyChanged;
            Lines.Add(viewModel);
        }

        OnPropertyChanged(nameof(CanConfirmInvoice));
        RefreshCommandStates();
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
        if (eventArgs.PropertyName == nameof(SupplierInvoiceLineViewModel.IsApproved))
        {
            OnPropertyChanged(nameof(CanConfirmInvoice));
            ConfirmInvoiceCommand.NotifyCanExecuteChanged();
        }
    }

    private void ClearLines()
    {
        foreach (var line in Lines)
        {
            line.PropertyChanged -= OnLinePropertyChanged;
        }

        Lines.Clear();
    }

    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanSelectFile));
        OnPropertyChanged(nameof(CanLookupSupplier));
        OnPropertyChanged(nameof(CanCreateSupplier));
        OnPropertyChanged(nameof(CanStageInvoice));
        OnPropertyChanged(nameof(CanConfirmInvoice));
        OnPropertyChanged(nameof(CanScanInvoice));
        OnPropertyChanged(nameof(IsCurrencyInputEnabled));
        OnPropertyChanged(nameof(IsRateInputEnabled));
        SelectFileCommand.NotifyCanExecuteChanged();
        LookupSupplierCommand.NotifyCanExecuteChanged();
        CreateSupplierCommand.NotifyCanExecuteChanged();
        StageInvoiceCommand.NotifyCanExecuteChanged();
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();
        ScanInvoiceCommand.NotifyCanExecuteChanged();
    }

    private void ShowAccessDenied() =>
        _dialogService.ShowWarning("Acceso denegado", "El rol actual no puede modificar el catálogo de productos.");

    private static string? NormalizeInput(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Precarga la tasa vigente solo si el usuario aun no escribio una (Bs.S).</summary>
    private async Task PrefillAppliedRateAsync()
    {
        try
        {
            var currentRate = await _exchangeRateService.GetCurrentRateAsync();
            if (currentRate.Rate > 0m && IsBsSCurrency && string.IsNullOrWhiteSpace(AppliedRateText))
            {
                AppliedRateText = currentRate.Rate.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception)
        {
            // Sin tasa vigente la factura Bs.S queda bloqueada; el usuario puede escribirla a mano.
        }
    }

    /// <summary>Acepta tasa con coma o punto decimal ("36,50" / "36.50").</summary>
    private static bool TryParseAppliedRate(string? value, out decimal rate)
    {
        rate = 0m;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        var commaIndex = normalized.LastIndexOf(',');
        var dotIndex = normalized.LastIndexOf('.');
        if (commaIndex >= 0 && dotIndex >= 0)
        {
            normalized = commaIndex > dotIndex
                ? normalized.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : normalized.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (commaIndex >= 0)
        {
            normalized = normalized.Replace(',', '.');
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out rate);
    }
}
