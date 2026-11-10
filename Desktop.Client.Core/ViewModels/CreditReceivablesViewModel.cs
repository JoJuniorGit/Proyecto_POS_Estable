using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (F3, S3): dashboard global de Cuentas por Cobrar. La cartera y los totales los calcula el
/// servidor; la UI solo filtra, presenta alertas de mora y orquesta abonos, anulación, devolución
/// y comprobantes. El dinero se muestra en USD y Bs.S a la tasa del día sin recalcular políticas.
/// </summary>
public partial class CreditReceivablesViewModel : ObservableObject, IDisposable
{
    private const string AllStatuses = "Todos";
    private const string DefaultCustomerFilterNote = "Busque por cliente, cédula/RIF o factura.";

    private readonly ISalesService _salesService;
    private readonly IPaymentService _paymentService;
    private readonly IDialogService _dialogService;
    private readonly UserSession? _userSession;
    private int _loadGeneration;

    public CreditReceivablesViewModel(
        ISalesService salesService,
        IPaymentService paymentService,
        IDialogService dialogService,
        UserSession? userSession = null)
    {
        _salesService = salesService ?? throw new ArgumentNullException(nameof(salesService));
        _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _userSession = userSession;
        ReceiptOpener = OpenPdfAsync;
    }

    public IReadOnlyList<string> StatusOptions { get; } = new[]
    {
        AllStatuses, "Vigente", "Parcial", "Vencida", "Pagada", "Anulada"
    };

    public ObservableCollection<CreditReceivableClientItemDto> Receivables { get; } = new();

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedStatus = AllStatuses;
    [ObservableProperty] private bool _overdueOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private decimal _totalOpenUSD;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverdueAlert))]
    [NotifyPropertyChangedFor(nameof(OverdueAlertMessage))]
    private decimal _totalOverdueUSD;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private int _openCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitle))]
    private decimal _todayRate;

    [ObservableProperty] private string? _successMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private CreditReceivableClientItemDto? _selectedReceivable;

    public bool CanManageCreditReturns => _userSession?.IsAdmin == true || _userSession?.IsManager == true;

    public bool HasOverdueAlert => TotalOverdueUSD > 0m;

    public string OverdueAlertMessage => HasOverdueAlert
        ? $"Alerta de mora: {CurrencyDisplay.FormatPrimary(TotalOverdueUSD)} vencidos en la cartera filtrada."
        : string.Empty;

    public string HeaderSubtitle =>
        $"Cartera abierta: {OpenCount} obligaciones · Tasa del día: {CurrencyDisplay.LocalCurrencyCode} {TodayRate:N2} por {CurrencyDisplay.PrimaryCurrencyCode} · {DefaultCustomerFilterNote}";

    /// <summary>Seam de pruebas para no abrir procesos externos al verificar comprobantes.</summary>
    public Func<byte[], string, Task> ReceiptOpener { get; set; }

    public async Task EnsureLoadedAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        int generation = ++_loadGeneration;
        IsLoading = true;
        SuccessMessage = null;
        ErrorMessage = null;

        try
        {
            var result = await _salesService.GetCreditReceivablesAsync(BuildFilter());
            if (generation != _loadGeneration) return;

            Receivables.Clear();
            foreach (var item in result.Items)
            {
                Receivables.Add(item);
            }

            TotalOpenUSD = result.TotalOpenUSD;
            TotalOverdueUSD = result.TotalOverdueUSD;
            OpenCount = result.OpenCount;
            TodayRate = result.TodayRate;
            HasLoaded = true;
            OnPropertyChanged(nameof(HeaderSubtitle));
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // La sesión la gestiona MainViewModel; no se muestra error ruidoso.
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error al cargar la cartera CxC: {ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration) IsLoading = false;
        }
    }

    private CreditReceivablesFilterClientDto BuildFilter() => new()
    {
        CustomerId = null,
        Status = string.Equals(SelectedStatus, AllStatuses, StringComparison.Ordinal) ? null : SelectedStatus,
        OverdueOnly = OverdueOnly,
        Search = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim()
    };

    [RelayCommand]
    public Task RefreshAsync() => EnsureLoadedAsync();

    [RelayCommand]
    public Task ApplyFiltersAsync() => EnsureLoadedAsync();

    [RelayCommand]
    public async Task ClearFiltersAsync()
    {
        SearchQuery = string.Empty;
        SelectedStatus = AllStatuses;
        OverdueOnly = false;
        await EnsureLoadedAsync();
    }

    [RelayCommand]
    public async Task RegisterPaymentAsync(CreditReceivableClientItemDto? row)
    {
        if (row == null) return;
        if (!row.IsOpen)
        {
            _dialogService.ShowWarning("Abono CxC", "La obligación ya está liquidada o anulada; no admite abonos.");
            return;
        }

        try
        {
            var book = await _salesService.GetCreditReceivablesAsync(new CreditReceivablesFilterClientDto
            {
                CustomerId = row.CustomerId
            });
            var openReceivables = book.Items.Where(item => item.IsOpen).ToList();
            var methods = (await _paymentService.GetActiveMethodsAsync()).ToList();

            var dialogVm = new CreditPaymentDialogViewModel(
                _salesService,
                _dialogService,
                row.CustomerId,
                row.CustomerName,
                openReceivables,
                methods,
                book.TodayRate,
                row.BalanceUSD);

            var result = await _dialogService.ShowModalAsync(dialogVm, "RootDialog") as CreditPaymentResultClientDto;
            if (result == null) return;

            await EnsureLoadedAsync();

            SuccessMessage = $"Abono registrado exitosamente (comprobante N° {result.PaymentId}): " +
                $"{CurrencyDisplay.FormatPrimary(result.AmountUSD)} ({CurrencyDisplay.FormatSecondary(result.AmountBsS)} a la tasa {result.PaymentRate:N2}).";

            if (_dialogService.ShowSuccessDialog(SuccessMessage, "Imprimir Comprobante"))
            {
                await PrintPaymentReceiptAsync(result.PaymentId);
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Abono CxC", $"No se pudo registrar el abono: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ViewStatementAsync(CreditReceivableClientItemDto? row)
    {
        if (row == null) return;

        try
        {
            var statement = await _salesService.GetCreditStatementAsync(row.CustomerId);
            var dialogVm = new CreditStatementDialogViewModel(statement, _dialogService);
            await _dialogService.ShowModalAsync(dialogVm, "RootDialog");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Estado de Cuenta", $"No se pudo cargar el estado de cuenta: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PrintCreditSaleReceiptAsync(CreditReceivableClientItemDto? row)
    {
        if (row == null) return;

        try
        {
            var bytes = await _salesService.GetCreditSaleReceiptAsync(row.SaleId);
            if (bytes == null || bytes.Length == 0)
            {
                _dialogService.ShowWarning("Comprobante de Crédito", "La venta no posee un comprobante de crédito disponible.");
                return;
            }

            await ReceiptOpener(bytes, $"Comprobante_Venta_Credito_{row.InvoiceDisplay}.pdf");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Comprobante de Crédito", $"No se pudo abrir el comprobante: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PrintPaymentReceiptAsync(int paymentId)
    {
        try
        {
            var bytes = await _salesService.GetCreditPaymentReceiptAsync(paymentId);
            if (bytes == null || bytes.Length == 0)
            {
                _dialogService.ShowWarning("Comprobante de Abono", "El abono no posee un comprobante disponible.");
                return;
            }

            await ReceiptOpener(bytes, $"Comprobante_Abono_{paymentId}.pdf");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Comprobante de Abono", $"No se pudo abrir el comprobante: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CancelAsync(CreditReceivableClientItemDto? row)
    {
        if (row == null) return;

        if (!row.IsCancelable)
        {
            _dialogService.ShowWarning(
                "Anulación CxC",
                "La anulación simple solo procede cuando la obligación no tiene abonos. " +
                "Con abonos registrados utilice la devolución para ajustar el saldo.");
            return;
        }

        if (!_dialogService.ShowConfirm(
                "Anulación CxC",
                $"¿Desea anular la venta a crédito {row.InvoiceDisplay} del cliente '{row.CustomerName}'? " +
                "Se restituirán el stock y el cupo, sin movimientos de caja."))
        {
            return;
        }

        try
        {
            await _salesService.CancelCreditSaleAsync(row.SaleId, "Anulación desde la cartera CxC");
            await EnsureLoadedAsync();
            SuccessMessage = $"Venta a crédito {row.InvoiceDisplay} anulada correctamente. El cupo fue restituido.";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Anulación CxC", $"No se pudo anular la venta a crédito: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ReturnAsync(CreditReceivableClientItemDto? row)
    {
        if (row == null) return;

        if (!CanManageCreditReturns)
        {
            _dialogService.ShowWarning(
                "Devolución CxC",
                "La devolución de ventas a crédito requiere autorización de un Administrador o Supervisor.");
            return;
        }

        try
        {
            var sale = await _salesService.GetSaleAsync(row.SaleId);
            var dialogVm = new CreditReturnDialogViewModel(_salesService, _dialogService, row, sale);
            var result = await _dialogService.ShowModalAsync(dialogVm, "RootDialog") as CreditReturnClientResultDto;
            if (result == null) return;

            await EnsureLoadedAsync();

            SuccessMessage = result.RefundedUSD > 0m
                ? $"Devolución registrada. La caja reembolsó {CurrencyDisplay.FormatPrimary(result.RefundedUSD)} ({CurrencyDisplay.FormatSecondary(result.RefundedBsS)}). Saldo pendiente: {CurrencyDisplay.FormatPrimary(result.BalanceAfterUSD)}."
                : $"Devolución registrada. Saldo pendiente actualizado: {CurrencyDisplay.FormatPrimary(result.BalanceAfterUSD)}.";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Devolución CxC", $"No se pudo registrar la devolución: {ex.Message}");
        }
    }

    private static async Task OpenPdfAsync(byte[] bytes, string fileName)
    {
        await Task.Yield();

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CommandCenterReceipts");
        System.IO.Directory.CreateDirectory(dir);
        string safeName = string.IsNullOrWhiteSpace(fileName)
            ? $"Comprobante_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            : fileName;
        string path = System.IO.Path.Combine(dir, safeName);
        System.IO.File.WriteAllBytes(path, bytes);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        // Sin suscripciones ni recursos retenidos: el ViewModel se conserva por sesión.
    }
}

