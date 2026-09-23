using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Core.Logging;

namespace Desktop.Client.ViewModels;

public partial class ClosureDetailRow : ObservableObject
{
    private readonly Action _onChanged;

    public ClosureDetailRow(int paymentMethodId, string paymentMethodName, decimal expectedAmountBsS, Action onChanged)
    {
        _paymentMethodId = paymentMethodId;
        _paymentMethodName = paymentMethodName;
        _expectedAmountBsS = expectedAmountBsS;
        _onChanged = onChanged;
    }

    private int _paymentMethodId;
    public int PaymentMethodId => _paymentMethodId;

    private string _paymentMethodName;
    public string PaymentMethodName => _paymentMethodName;

    private decimal _expectedAmountBsS;
    public decimal ExpectedAmountBsS
    {
        get => _expectedAmountBsS;
        set => SetProperty(ref _expectedAmountBsS, value);
    }

    private decimal _actualAmountBsS;
    public decimal ActualAmountBsS
    {
        get => _actualAmountBsS;
        set
        {
            if (SetProperty(ref _actualAmountBsS, value))
            {
                OnPropertyChanged(nameof(DifferenceBsS));
                OnPropertyChanged(nameof(DifferenceColor));
                OnPropertyChanged(nameof(DifferenceDisplay));
                _onChanged?.Invoke();
            }
        }
    }

    public decimal DifferenceBsS => ActualAmountBsS - ExpectedAmountBsS;

    public string DifferenceColor => DifferenceBsS switch
    {
        < 0 => "#EF4444",
        > 0 => "#10B981",
        _ => "#94A3B8"
    };

    public string DifferenceDisplay => DifferenceBsS switch
    {
        > 0 => $"+{DifferenceBsS:N2}",
        < 0 => $"{DifferenceBsS:N2}",
        _ => "0.00"
    };
}

public partial class DailyClosureViewModel : ObservableObject
{
    private readonly IDailyClosureClientService _closureService;
    private readonly IDialogService _dialogService;
    private readonly IFilePickerDialog _filePicker;
    public UserSession? UserSession { get; }

    public bool CanToggleBlindClosing => UserSession?.IsAdmin == true;

    public DailyClosureViewModel(IDailyClosureClientService closureService, IDialogService dialogService, UserSession? userSession = null, IFilePickerDialog? filePicker = null)
    {
        _closureService = closureService;
        _dialogService = dialogService;
        _filePicker = filePicker ?? new NoopFilePicker();
        UserSession = userSession;

        // Forced true for cashiers, default false for admins
        _isBlindClosing = UserSession?.IsCashier == true;
    }

    // 8.6-M7: instante único de cierre en la zona legal (Venezuela). La API recibe UTC y el
    // comprobante/fecha se muestran en hora local legal para no mezclar ahora-UTC con DateTime.Now
    // del equipo (que podría estar en otra zona y desalinear fecha del arqueo).
    private DateTime LocalClosureNow()
    {
        var utcNow = DateTime.UtcNow;
        try
        {
            var tz = Core.Helpers.TimeZoneHelper.GetTimeZone(null);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        }
        catch
        {
            return DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).ToLocalTime();
        }
    }

    public ObservableCollection<ClosureDetailRow> DetailRows { get; } = new();

    private bool _isBlindClosing;
    public bool IsBlindClosing
    {
        get => _isBlindClosing;
        set
        {
            if (!CanToggleBlindClosing && !value)
            {
                // Prevent Cashiers from disabling blind closing
                return;
            }
            if (SetProperty(ref _isBlindClosing, value))
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
        set
        {
            if (SetProperty(ref _observation, value))
            {
                OnPropertyChanged(nameof(ObservationCounterText));
            }
        }
    }

    public string ObservationCounterText => $"{Observation?.Length ?? 0} / 500";

    private decimal _totalExpectedBsS;
    public decimal TotalExpectedBsS
    {
        get => _totalExpectedBsS;
        set => SetProperty(ref _totalExpectedBsS, value);
    }

    private decimal _totalActualBsS;
    public decimal TotalActualBsS
    {
        get => _totalActualBsS;
        set => SetProperty(ref _totalActualBsS, value);
    }

    private decimal _totalDifferenceBsS;
    public decimal TotalDifferenceBsS
    {
        get => _totalDifferenceBsS;
        set => SetProperty(ref _totalDifferenceBsS, value);
    }

    public string DifferenceStatusLabel => TotalDifferenceBsS > 0
        ? $"SOBRANTE EN CAJA (+{TotalDifferenceBsS:N2} Bs.S)"
        : (TotalDifferenceBsS < 0 ? $"FALTANTE EN CAJA ({TotalDifferenceBsS:N2} Bs.S)" : "CUADRADO EXACTO");

    public string DifferenceStatusColor => TotalDifferenceBsS > 0
        ? "#10B981"
        : (TotalDifferenceBsS < 0 ? "#EF4444" : "#3B82F6");

    public string DifferenceCardBackground => TotalDifferenceBsS switch
    {
        < 0 => "#FEF2F2",
        > 0 => "#F0FDF4",
        _ => "#F8FAFC"
    };

    public string DifferenceCardBorder => TotalDifferenceBsS switch
    {
        < 0 => "#FECACA",
        > 0 => "#BBF7D0",
        _ => "#E2E8F0"
    };

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isSaved;
    public bool IsSaved
    {
        get => _isSaved;
        set => SetProperty(ref _isSaved, value);
    }

    private void RecalculateTotals()
    {
        TotalExpectedBsS = DetailRows.Sum(r => r.ExpectedAmountBsS);
        TotalActualBsS = DetailRows.Sum(r => r.ActualAmountBsS);
        TotalDifferenceBsS = TotalActualBsS - TotalExpectedBsS;
        OnPropertyChanged(nameof(DifferenceStatusLabel));
        OnPropertyChanged(nameof(DifferenceStatusColor));
        OnPropertyChanged(nameof(DifferenceCardBackground));
        OnPropertyChanged(nameof(DifferenceCardBorder));
    }

    [RelayCommand]
    public async Task LoadExpectedTotalsAsync()
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        IsLoading = true;
        IsSaved = false;
        try
        {
            var totals = await _closureService.GetExpectedTotalsAsync(DateTime.UtcNow);

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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.Message.Contains("401"))
        {
            // El cierre de sesión y redirección a login son gestionados centralizadamente por UserSessionHeaderHandler.
            ClientStateLogger.LogWarning("[DAILY_CLOSURE] Petición no autorizada al cargar totales esperados. La sesión fue cerrada.", nameof(DailyClosureViewModel));
        }
        catch (OperationCanceledException)
        {
            // Petición cancelada o reemplazada
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
        var userName = UserSession?.CurrentUser?.Name ?? UserSession?.CurrentUser?.Cedula ?? "Usuario";
        var dateStr = LocalClosureNow().ToString("dd/MM/yyyy HH:mm");

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
                sb.AppendLine($"• {row.PaymentMethodName}: {row.ActualAmountBsS:N2} Bs.S");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"TOTAL DECLARADO: {TotalActualBsS:N2} Bs.S");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Observaciones: {Observation}");
            }
            sb.AppendLine("\n¿Desea confirmar y registrar este arqueo a ciegas?");
            return sb.ToString();
        }
        else
        {
            // Admin Mode (Detailed)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== RESUMEN DE CIERRE DIARIO (AUDITORÍA) ===");
            sb.AppendLine($"Fecha: {dateStr}");
            sb.AppendLine($"Administrador: {userName}");
            sb.AppendLine();
            sb.AppendLine("DETALLE POR MÉTODO DE PAGO:");
            foreach (var row in DetailRows)
            {
                var diffSign = row.DifferenceBsS >= 0 ? "+" : "";
                sb.AppendLine($"• {row.PaymentMethodName}:");
                sb.AppendLine($"    Esperado:  {row.ExpectedAmountBsS:N2} Bs.S");
                sb.AppendLine($"    Declarado: {row.ActualAmountBsS:N2} Bs.S");
                sb.AppendLine($"    Diferencia: {diffSign}{row.DifferenceBsS:N2} Bs.S");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"TOTAL ESPERADO:  {TotalExpectedBsS:N2} Bs.S");
            sb.AppendLine($"TOTAL DECLARADO: {TotalActualBsS:N2} Bs.S");
            var totalDiffSign = TotalDifferenceBsS >= 0 ? "+" : "";
            sb.AppendLine($"DIFERENCIA NETA: {totalDiffSign}{TotalDifferenceBsS:N2} Bs.S ({DifferenceStatusLabel})");
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Observaciones: {Observation}");
            }
            sb.AppendLine("\n¿Desea confirmar y registrar este cierre contable?");
            return sb.ToString();
        }
    }

    [RelayCommand]
    private async Task ConfirmClosure()
    {
        if (!DetailRows.Any())
        {
            _dialogService.ShowWarning("Advertencia", "No hay datos de métodos de pago para procesar el cierre.");
            return;
        }

        string confirmMessage = BuildConfirmationMessage();
        string dialogTitle = IsBlindClosing ? "Confirmar Arqueo a Ciegas" : "Confirmar Cierre Diario (Admin)";

        bool confirmed = _dialogService.ShowConfirm(dialogTitle, confirmMessage);
        if (!confirmed) return;

        IsLoading = true;
        try
        {
            var request = new Services.CreateClosureRequest
            {
                ClosureDate = DateTime.UtcNow,
                UserId = UserSession?.CurrentUser?.Name ?? UserSession?.CurrentUser?.Cedula ?? "Admin",
                Observation = Observation,
                Details = DetailRows.Select(r => new Services.CreateClosureDetailRequest
                {
                    PaymentMethodId = r.PaymentMethodId,
                    PaymentMethodName = r.PaymentMethodName,
                    ExpectedAmountBsS = r.ExpectedAmountBsS,
                    ActualAmountBsS = r.ActualAmountBsS
                }).ToList()
            };

            await _closureService.CreateClosureAsync(request);
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

        var userName = UserSession?.CurrentUser?.Name ?? UserSession?.CurrentUser?.Cedula ?? "Usuario";
        var localNow = LocalClosureNow();
        var dateStr = localNow.ToString("dd/MM/yyyy HH:mm:ss");

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
            var savePath = _filePicker.PickSaveFilePath(
                "Guardar Comprobante de Cierre",
                "Archivo de Texto (*.txt)|*.txt",
                $"Comprobante_Cierre_{localNow:yyyyMMdd_HHmmss}.txt");

            if (!string.IsNullOrEmpty(savePath))
            {
                System.IO.File.WriteAllText(savePath, sb.ToString());
                _dialogService.ShowInfo("Comprobante Guardado", $"El comprobante se guardó correctamente en:\n{savePath}");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error de Exportación", $"No se pudo guardar el comprobante: {ex.Message}");
        }
    }
}
