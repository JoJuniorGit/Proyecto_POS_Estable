using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// T4 (Paso 13 / 8.188, S1/S2/S5/S7): acciones de caja chica dentro de la vista de caja. El
/// listado y los saldos físicos pertenecen al turno activo; el registro y la anulación se
/// delegan a sus modales, que orquestan la autorización jerárquica. No recalcula políticas ni
/// tasas: solo refleja los snapshots que devuelve el servidor.
/// </summary>
public partial class CashDrawerViewModel
{
    private const string PettyCashVoucherTempFolder = "CommandCenterReceipts";

    public ObservableCollection<PettyCashExpenseRowViewModel> PettyCashExpenses { get; } = new();

    [ObservableProperty]
    private bool _isPettyCashPanelOpen;

    [ObservableProperty]
    private bool _isPettyCashLoading;

    [ObservableProperty]
    private decimal _pettyCashPhysicalBalanceBsS;

    [ObservableProperty]
    private decimal _pettyCashPhysicalBalanceUsd;

    /// <summary>Roles habilitados por el contrato del endpoint (Driver queda fuera).</summary>
    public bool CanUsePettyCash => IsSessionActive
        && (_userSession?.IsAdmin == true || _userSession?.IsManager == true || _userSession?.IsCashier == true);

    /// <summary>El cierre de la sesión de caja se reserva a la supervisión.</summary>
    public bool CanCloseCashDrawer => IsSessionActive
        && (_userSession?.IsAdmin == true || _userSession?.IsManager == true);

    public bool HasPettyCashExpenses => PettyCashExpenses.Count > 0;

    /// <summary>Bs.S 2dp / USD 4dp; el arqueo ciego oculta el teórico a los no supervisores.</summary>
    public string PettyCashPhysicalBalanceBsSDisplay => CanViewTheoreticalBalance
        ? $"Bs.S {PettyCashLabels.FormatBsS(PettyCashPhysicalBalanceBsS)}"
        : "—";

    public string PettyCashPhysicalBalanceUsdDisplay => CanViewTheoreticalBalance
        ? PettyCashLabels.FormatUsd(PettyCashPhysicalBalanceUsd)
        : "—";

    public string PettyCashSummary
    {
        get
        {
            var active = PettyCashExpenses
                .Where(row => row.Expense.Status == PettyCashStatusClient.Active)
                .ToList();
            decimal totalBsS = active.Sum(row => row.Expense.AmountBsS);
            decimal totalUsd = active.Sum(row => row.Expense.AmountUsd);
            return $"{active.Count} activo(s) · Bs.S {PettyCashLabels.FormatBsS(totalBsS)} · {PettyCashLabels.FormatUsd(totalUsd)}";
        }
    }

    /// <summary>Seam de pruebas para no abrir visores PDF reales.</summary>
    public Func<byte[], string, Task> PettyCashVoucherOpener { get; set; } = OpenPettyCashVoucherPdfAsync;

    [RelayCommand]
    private async Task TogglePettyCashPanelAsync()
    {
        if (!CanUsePettyCash)
        {
            _dialogService?.ShowWarning("Caja Chica", "La caja chica requiere una sesión de caja activa y un rol Admin, Gerente o Cajero.");
            return;
        }

        IsPettyCashPanelOpen = !IsPettyCashPanelOpen;
        if (IsPettyCashPanelOpen)
        {
            await LoadPettyCashAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshPettyCashAsync() => await LoadPettyCashAsync();

    /// <summary>
    /// Carga los gastos del turno y, solo para supervisores, los saldos físicos por moneda. El
    /// cajero conserva el arqueo ciego: el servidor sigue validando la liquidez al registrar.
    /// </summary>
    public async Task LoadPettyCashAsync()
    {
        var session = ActiveSession;
        if (session is null)
        {
            PettyCashExpenses.Clear();
            PettyCashPhysicalBalanceBsS = 0m;
            PettyCashPhysicalBalanceUsd = 0m;
            NotifyPettyCashChanged();
            return;
        }

        IsPettyCashLoading = true;
        try
        {
            var rows = await _cashDrawerService.GetPettyCashExpensesAsync(session.Id);
            PettyCashExpenses.Clear();
            foreach (var row in rows)
            {
                PettyCashExpenses.Add(new PettyCashExpenseRowViewModel(row));
            }

            if (CanViewTheoreticalBalance)
            {
                var balances = await _cashDrawerService.GetPhysicalBalancesAsync(session.Id);
                PettyCashPhysicalBalanceBsS = balances.BsS;
                PettyCashPhysicalBalanceUsd = balances.Usd;
            }
            else
            {
                PettyCashPhysicalBalanceBsS = 0m;
                PettyCashPhysicalBalanceUsd = 0m;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // La sesión la gestiona centralizadamente UserSessionHeaderHandler.
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Caja Chica", $"No se pudieron cargar los gastos del turno: {ex.Message}");
        }
        finally
        {
            IsPettyCashLoading = false;
            NotifyPettyCashChanged();
        }
    }

    [RelayCommand]
    private async Task RegisterPettyCashExpenseAsync()
    {
        if (ActiveSession is null || _dialogService is null) return;
        if (!CanUsePettyCash)
        {
            _dialogService.ShowWarning("Caja Chica", "No tiene permisos para registrar gastos de caja chica en esta sesión.");
            return;
        }

        var rate = _exchangeRateService.CurrentRate;
        if (rate <= 0m)
        {
            _dialogService.ShowWarning("Caja Chica", "Configure la tasa de cambio vigente antes de registrar el gasto.");
            return;
        }

        try
        {
            var dialogVm = new PettyCashExpenseDialogViewModel(
                _cashDrawerService,
                _dialogService,
                ActiveSession.Id,
                rate);
            var result = await _dialogService.ShowPettyCashExpenseDialogAsync(dialogVm);
            if (result is null) return;

            await LoadSessionAsync();

            string message =
                $"Gasto {result.VoucherCode} registrado: {PettyCashLabels.Category(result.Category)} · " +
                $"Bs.S {PettyCashLabels.FormatBsS(result.AmountBsS)} / {PettyCashLabels.FormatUsd(result.AmountUsd)}.";
            if (_dialogService.ShowSuccessDialog(message, "Imprimir Vale"))
            {
                await PrintVoucherAsync(result);
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Caja Chica", $"No se pudo registrar el gasto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CancelPettyCashExpenseAsync(PettyCashExpenseRowViewModel? row)
    {
        if (row is null || ActiveSession is null || _dialogService is null) return;

        if (!row.CanCancel)
        {
            _dialogService.ShowWarning("Caja Chica", "Solo los gastos activos del turno pueden anularse.");
            return;
        }

        if (!IsSessionActive)
        {
            _dialogService.ShowWarning("Caja Chica", "La anulación requiere que la sesión de caja siga abierta.");
            return;
        }

        try
        {
            var dialogVm = new PettyCashCancelDialogViewModel(_cashDrawerService, _dialogService, row.Expense);
            var result = await _dialogService.ShowPettyCashCancelDialogAsync(dialogVm);
            if (result is null) return;

            await LoadSessionAsync();
            _dialogService.ShowInfo(
                "Caja Chica",
                $"Gasto {result.VoucherCode} anulado. La reversa compensatoria restituyó la gaveta con los snapshots originales.");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Caja Chica", $"No se pudo anular el gasto: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PrintPettyCashVoucherAsync(PettyCashExpenseRowViewModel? row)
    {
        if (row is null || _dialogService is null) return;
        await PrintVoucherAsync(row.Expense);
    }

    private async Task PrintVoucherAsync(PettyCashExpenseClientDto expense)
    {
        try
        {
            var bytes = await _cashDrawerService.GetPettyCashVoucherAsync(expense.Id);
            if (bytes is null || bytes.Length == 0)
            {
                _dialogService?.ShowWarning("Vale de Caja Chica", "El vale no está disponible para este gasto.");
                return;
            }

            await PettyCashVoucherOpener(bytes, $"{expense.VoucherCode}.pdf");
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Vale de Caja Chica", $"No se pudo generar el vale: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CloseCashDrawerAsync()
    {
        if (ActiveSession is null || _dialogService is null) return;

        if (!CanCloseCashDrawer)
        {
            _dialogService.ShowWarning("Cierre de Caja", "Solo un Administrador o Gerente puede cerrar la sesión de caja.");
            return;
        }

        var rate = _exchangeRateService.CurrentRate;
        if (rate <= 0m)
        {
            _dialogService.ShowWarning("Cierre de Caja", "Configure la tasa de cambio vigente antes de cerrar la caja.");
            return;
        }

        try
        {
            // El teórico USD del arqueo se consulta al abrir el diálogo; el panel puede estar
            // cerrado y el conteo debe reflejar la gaveta dura real del turno.
            decimal? theoreticalUsd = null;
            if (CanViewTheoreticalBalance && ActiveSession is { } session)
            {
                try
                {
                    var balances = await _cashDrawerService.GetPhysicalBalancesAsync(session.Id);
                    if (balances is not null)
                    {
                        PettyCashPhysicalBalanceBsS = balances.BsS;
                        PettyCashPhysicalBalanceUsd = balances.Usd;
                    }
                }
                catch (Exception)
                {
                    // Informativo: un fallo de lectura no bloquea el cierre; el servidor valida.
                }

                theoreticalUsd = PettyCashPhysicalBalanceUsd;
            }

            var result = await _dialogService.ShowCashDrawerCloseDialogAsync(rate, CurrentBalanceBsS, theoreticalUsd);
            if (result is null) return;

            await _cashDrawerService.CloseSessionAsync(
                result.ActualClosingBalanceLocal,
                rate,
                result.ActualClosingBalanceUsd);
            await LoadSessionAsync();
            _dialogService.ShowInfo("Cierre de Caja", "La sesión de caja se cerró correctamente.");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Cierre de Caja", $"No se pudo cerrar la sesión de caja: {ex.Message}");
        }
    }

    private void NotifyPettyCashChanged()
    {
        OnPropertyChanged(nameof(HasPettyCashExpenses));
        OnPropertyChanged(nameof(PettyCashSummary));
    }

    partial void OnPettyCashPhysicalBalanceBsSChanged(decimal value) =>
        OnPropertyChanged(nameof(PettyCashPhysicalBalanceBsSDisplay));

    partial void OnPettyCashPhysicalBalanceUsdChanged(decimal value) =>
        OnPropertyChanged(nameof(PettyCashPhysicalBalanceUsdDisplay));

    private static async Task OpenPettyCashVoucherPdfAsync(byte[] bytes, string fileName)
    {
        await Task.Yield();

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), PettyCashVoucherTempFolder);
        System.IO.Directory.CreateDirectory(dir);
        string safeName = string.IsNullOrWhiteSpace(fileName)
            ? $"Vale_Caja_Chica_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            : fileName;
        string path = System.IO.Path.Combine(dir, safeName);
        await System.IO.File.WriteAllBytesAsync(path, bytes);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
