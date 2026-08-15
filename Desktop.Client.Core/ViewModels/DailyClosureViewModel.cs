using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class ClosureDetailRow : ObservableObject
{
    private readonly Action _on_changed;

    public ClosureDetailRow(int payment_method_id, string payment_method_name, decimal expected_amount_bs_s, Action on_changed)
    {
        _payment_method_id = payment_method_id;
        _payment_method_name = payment_method_name;
        _expected_amount_bs_s = expected_amount_bs_s;
        _on_changed = on_changed;
    }

    private int _payment_method_id;
    public int PaymentMethodId => _payment_method_id;

    private string _payment_method_name;
    public string PaymentMethodName => _payment_method_name;

    private decimal _expected_amount_bs_s;
    public decimal ExpectedAmountBsS
    {
        get => _expected_amount_bs_s;
        set => SetProperty(ref _expected_amount_bs_s, value);
    }

    private decimal _actual_amount_bs_s;
    public decimal ActualAmountBsS
    {
        get => _actual_amount_bs_s;
        set
        {
            if (SetProperty(ref _actual_amount_bs_s, value))
            {
                OnPropertyChanged(nameof(DifferenceBsS));
                _on_changed?.Invoke();
            }
        }
    }

    public decimal DifferenceBsS => ActualAmountBsS - ExpectedAmountBsS;
}

public partial class DailyClosureViewModel : ObservableObject
{
    private readonly IDailyClosureClientService _closure_service;
    private readonly IDialogService _dialogService;
    public UserSession UserSession { get; }

    public bool CanToggleBlindClosing => UserSession.IsAdmin;

    public DailyClosureViewModel(IDailyClosureClientService closure_service, IDialogService dialogService, UserSession userSession)
    {
        _closure_service = closure_service;
        _dialogService = dialogService;
        UserSession = userSession;

        // Forced true for cashiers, default false for admins
        _is_blind_closing = UserSession.IsCashier;

        _ = LoadExpectedTotalsAsync();
    }

    public ObservableCollection<ClosureDetailRow> DetailRows { get; } = new();

    private bool _is_blind_closing;
    public bool IsBlindClosing
    {
        get => _is_blind_closing;
        set
        {
            if (!CanToggleBlindClosing && !value)
            {
                // Prevent Cashiers from disabling blind closing
                return;
            }
            if (SetProperty(ref _is_blind_closing, value))
            {
                OnPropertyChanged(nameof(DifferenceStatusLabel));
                OnPropertyChanged(nameof(DifferenceStatusColor));
            }
        }
    }

    private string? _observation;
    public string? Observation
    {
        get => _observation;
        set => SetProperty(ref _observation, value);
    }

    private decimal _total_expected_bs_s;
    public decimal TotalExpectedBsS
    {
        get => _total_expected_bs_s;
        set => SetProperty(ref _total_expected_bs_s, value);
    }

    private decimal _total_actual_bs_s;
    public decimal TotalActualBsS
    {
        get => _total_actual_bs_s;
        set => SetProperty(ref _total_actual_bs_s, value);
    }

    private decimal _total_difference_bs_s;
    public decimal TotalDifferenceBsS
    {
        get => _total_difference_bs_s;
        set => SetProperty(ref _total_difference_bs_s, value);
    }

    public string DifferenceStatusLabel => TotalDifferenceBsS > 0
        ? $"SOBRANTE EN CAJA (+{TotalDifferenceBsS:N2} Bs.S)"
        : (TotalDifferenceBsS < 0 ? $"FALTANTE EN CAJA ({TotalDifferenceBsS:N2} Bs.S)" : "CUADRADO EXACTO");

    public string DifferenceStatusColor => TotalDifferenceBsS > 0
        ? "#10B981"
        : (TotalDifferenceBsS < 0 ? "#EF4444" : "#3B82F6");

    private bool _is_loading;
    public bool IsLoading
    {
        get => _is_loading;
        set => SetProperty(ref _is_loading, value);
    }

    private bool _is_saved;
    public bool IsSaved
    {
        get => _is_saved;
        set => SetProperty(ref _is_saved, value);
    }

    private void RecalculateTotals()
    {
        TotalExpectedBsS = DetailRows.Sum(r => r.ExpectedAmountBsS);
        TotalActualBsS = DetailRows.Sum(r => r.ActualAmountBsS);
        TotalDifferenceBsS = TotalActualBsS - TotalExpectedBsS;
        OnPropertyChanged(nameof(DifferenceStatusLabel));
        OnPropertyChanged(nameof(DifferenceStatusColor));
    }

    [RelayCommand]
    public async Task LoadExpectedTotalsAsync()
    {
        IsLoading = true;
        IsSaved = false;
        try
        {
            var totals = await _closure_service.GetExpectedTotalsAsync(DateTime.UtcNow);

            DetailRows.Clear();
            foreach (var t in totals)
            {
                DetailRows.Add(new ClosureDetailRow(
                    t.PaymentMethodId,
                    t.PaymentMethodName,
                    t.ExpectedAmountBsS,
                    RecalculateTotals));
            }

            RecalculateTotals();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al cargar totales esperados: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string BuildConfirmationMessage()
    {
        var userName = UserSession.CurrentUser?.Name ?? UserSession.CurrentUser?.Cedula ?? "Usuario";
        var dateStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

        if (IsBlindClosing)
        {
            // Cashier Mode (Blind) - ONLY DECLARED AMOUNTS
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== RESUMEN DE ARQUEO A CIEGAS ===");
            sb.AppendLine($"Fecha: {dateStr}");
            sb.AppendLine($"Cajero: {userName}");
            sb.AppendLine();
            sb.AppendLine("DESGLOSE DE MONTOS DECLARADOS:");
            foreach (var row in DetailRows)
            {
                sb.AppendLine($"  • {row.PaymentMethodName}: Bs.S {row.ActualAmountBsS:N2}");
            }
            sb.AppendLine("------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO: Bs.S {TotalActualBsS:N2}");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Observaciones: {Observation}");
            }
            sb.AppendLine();
            sb.AppendLine("¿Confirma guardar este cierre de caja?");
            return sb.ToString();
        }
        else
        {
            // Admin Mode - FULL AUDIT BREAKDOWN
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== AUDITORÍA DE CIERRE DIARIO ===");
            sb.AppendLine($"Fecha: {dateStr}");
            sb.AppendLine($"Administrador: {userName}");
            sb.AppendLine();
            sb.AppendLine("DESGLOSE POR MÉTODO DE PAGO:");
            foreach (var row in DetailRows)
            {
                sb.AppendLine($"  • {row.PaymentMethodName}: Declarado Bs.S {row.ActualAmountBsS:N2} | Esperado Bs.S {row.ExpectedAmountBsS:N2} | Dif: Bs.S {row.DifferenceBsS:N2}");
            }
            sb.AppendLine("------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:  Bs.S {TotalActualBsS:N2}");
            sb.AppendLine($"TOTAL ESPERADO:   Bs.S {TotalExpectedBsS:N2}");
            sb.AppendLine($"DIFERENCIA TOTAL: Bs.S {TotalDifferenceBsS:N2} ({DifferenceStatusLabel})");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Observaciones: {Observation}");
            }
            sb.AppendLine();
            sb.AppendLine("¿Confirma guardar este cierre de caja en la base de datos?");
            return sb.ToString();
        }
    }

    [RelayCommand]
    private async Task ConfirmClosureAsync()
    {
        if (!DetailRows.Any())
        {
            _dialogService.ShowWarning("Advertencia", "No hay métodos de pago disponibles para cerrar.");
            return;
        }

        string confirmPrompt = BuildConfirmationMessage();
        if (!_dialogService.ShowConfirm("Confirmar Cierre Diario", confirmPrompt))
        {
            return;
        }

        IsLoading = true;
        try
        {
            var request = new Services.CreateClosureRequest
            {
                ClosureDate = DateTime.UtcNow,
                UserId = UserSession.CurrentUser?.Name ?? UserSession.CurrentUser?.Cedula ?? "Admin",
                Observation = Observation,
                Details = DetailRows.Select(r => new Services.CreateClosureDetailRequest
                {
                    PaymentMethodId = r.PaymentMethodId,
                    PaymentMethodName = r.PaymentMethodName,
                    ExpectedAmountBsS = r.ExpectedAmountBsS,
                    ActualAmountBsS = r.ActualAmountBsS
                }).ToList()
            };

            await _closure_service.CreateClosureAsync(request);
            IsSaved = true;
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Desktop.Client.Messages.ShiftClosedMessage());
            _dialogService.ShowInfo("Éxito de Cierre", "Cierre diario procesado y guardado exitosamente. Comprobantes guardados automáticamente en Descargas y en Documentos\\Registro de cierres.\n\nLos acumuladores de ingresos y egresos han sido reiniciados a 0.00 Bs.S para el nuevo turno.");
            await LoadExpectedTotalsAsync();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al guardar el cierre: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ExportClosureReceipt()
    {
        if (!DetailRows.Any())
        {
            _dialogService.ShowWarning("Advertencia", "No hay datos de cierre para exportar.");
            return;
        }

        var userName = UserSession.CurrentUser?.Name ?? UserSession.CurrentUser?.Cedula ?? "Usuario";
        var dateStr = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        var sb = new System.Text.StringBuilder();
        if (IsBlindClosing)
        {
            sb.AppendLine("=========================================");
            sb.AppendLine("      COMPROBANTE DE ARQUEO A CIEGAS     ");
            sb.AppendLine("=========================================");
            sb.AppendLine($"Fecha/Hora: {dateStr}");
            sb.AppendLine($"Cajero:     {userName}");
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine("MÉTODOS DE PAGO DECLARADOS:");
            foreach (var row in DetailRows)
            {
                sb.AppendLine($"  {row.PaymentMethodName,-22} Bs.S {row.ActualAmountBsS,10:N2}");
            }
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:         Bs.S {TotalActualBsS,10:N2}");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Notas: {Observation}");
            }
            sb.AppendLine("=========================================");
        }
        else
        {
            sb.AppendLine("=========================================");
            sb.AppendLine(" COMPROBANTE DE CIERRE Y AUDITORÍA DE CAJA ");
            sb.AppendLine("=========================================");
            sb.AppendLine($"Fecha/Hora:    {dateStr}");
            sb.AppendLine($"Administrador: {userName}");
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine("DETALLE DE ARQUEO DE MÉTODOS DE PAGO:");
            foreach (var row in DetailRows)
            {
                sb.AppendLine($"  {row.PaymentMethodName}");
                sb.AppendLine($"    Declarado: Bs.S {row.ActualAmountBsS:N2} | Esperado: Bs.S {row.ExpectedAmountBsS:N2} | Dif: Bs.S {row.DifferenceBsS:N2}");
            }
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:  Bs.S {TotalActualBsS,10:N2}");
            sb.AppendLine($"TOTAL ESPERADO:   Bs.S {TotalExpectedBsS,10:N2}");
            sb.AppendLine($"DIFERENCIA TOTAL: Bs.S {TotalDifferenceBsS,10:N2}");
            sb.AppendLine($"ESTADO DE CAJA:   {DifferenceStatusLabel}");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Notas: {Observation}");
            }
            sb.AppendLine("=========================================");
        }

        try
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Archivo de Texto (*.txt)|*.txt",
                FileName = $"Comprobante_Cierre_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (saveDialog.ShowDialog() == true)
            {
                System.IO.File.WriteAllText(saveDialog.FileName, sb.ToString());
                _dialogService.ShowInfo("Comprobante Guardado", $"El comprobante se guardó correctamente en:\n{saveDialog.FileName}");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error de Exportación", $"No se pudo guardar el comprobante: {ex.Message}");
        }
    }
}
