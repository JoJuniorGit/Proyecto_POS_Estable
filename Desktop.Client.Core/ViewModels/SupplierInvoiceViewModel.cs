using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Interfaces;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private int _disposed;

    public UserSession UserSession { get; }
    public bool CanMutateCatalog => UserSession.CanMutateCatalog;
    public ObservableCollection<SupplierInvoiceColumnMappingItem> ColumnMappings { get; } = new();
    public ObservableCollection<SupplierInvoiceLineViewModel> Lines { get; } = new();

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

    public bool CanSelectFile => CanMutateCatalog && !IsBusy;
    public bool CanLookupSupplier => CanMutateCatalog && !IsBusy &&
        (!string.IsNullOrWhiteSpace(SupplierRifOrNit) || !string.IsNullOrWhiteSpace(SupplierCommercialName));
    public bool CanCreateSupplier => CanMutateCatalog && !IsBusy && SelectedSupplier is null &&
        !string.IsNullOrWhiteSpace(SupplierCommercialName);
    public bool CanStageInvoice => CanMutateCatalog && !IsBusy && SelectedSupplier is not null &&
        !string.IsNullOrWhiteSpace(SelectedFilePath) && HasFileHeaders && HasValidColumnMapping();
    public bool CanConfirmInvoice => CanMutateCatalog && !IsBusy && StagedInvoice is not null &&
        Lines.Any(line => line.IsApproved && line.CanApprove);

    public SupplierInvoiceViewModel(
        ClientSupplierInvoiceService invoiceService,
        UserSession userSession,
        IDialogService dialogService,
        IFilePickerDialog filePicker)
    {
        ArgumentNullException.ThrowIfNull(invoiceService);
        ArgumentNullException.ThrowIfNull(userSession);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(filePicker);

        _invoiceService = invoiceService;
        UserSession = userSession;
        _dialogService = dialogService;
        _filePicker = filePicker;
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

    partial void OnStagedInvoiceChanged(SupplierInvoiceDetailDto? value)
    {
        OnPropertyChanged(nameof(CanConfirmInvoice));
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
            var request = new StageSupplierInvoiceRequestDto(
                SelectedSupplier.Id,
                SelectedSupplier.RifOrNit,
                SelectedSupplier.CommercialName,
                columnMapping,
                lines);
            var invoice = await _invoiceService.StageAsync(request, cancellationToken);
            var updatedSupplier = SelectedSupplier with { ColumnMapping = columnMapping };
            var supplierIndex = Suppliers.IndexOf(SelectedSupplier);
            if (supplierIndex >= 0)
            {
                Suppliers[supplierIndex] = updatedSupplier;
            }

            SelectedSupplier = updatedSupplier;
            LoadStagedInvoice(invoice);
            StatusMessage = $"Factura #{invoice.Id} preparada. Revise las líneas antes de confirmar.";
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
                .Select(line => new ConfirmLineDto(
                    line.LineId,
                    true,
                    line.MarginRetailOverride,
                    line.MarginWholesaleOverride))
                .ToArray());
            var result = await _invoiceService.ConfirmAsync(StagedInvoice.Id, request, cancellationToken);
            LoadStagedInvoice(result);
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
        foreach (var line in invoice.Lines)
        {
            var viewModel = new SupplierInvoiceLineViewModel(line);
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
        SelectFileCommand.NotifyCanExecuteChanged();
        LookupSupplierCommand.NotifyCanExecuteChanged();
        CreateSupplierCommand.NotifyCanExecuteChanged();
        StageInvoiceCommand.NotifyCanExecuteChanged();
        ConfirmInvoiceCommand.NotifyCanExecuteChanged();
    }

    private void ShowAccessDenied() =>
        _dialogService.ShowWarning("Acceso denegado", "El rol actual no puede modificar el catálogo de productos.");

    private static string? NormalizeInput(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
