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
using Core.Helpers;
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

    // 8.143: la diferencia mostrada honra el ajuste activo (Venezolano/Internacional) y se
    // re-formatea al vuelo cuando el formato cambia.
    public string DifferenceDisplay => DifferenceBsS switch
    {
        > 0 => $"+{CurrencyDisplay.Number(DifferenceBsS)}",
        < 0 => $"{CurrencyDisplay.Number(DifferenceBsS)}",
        _ => CurrencyDisplay.Number(0m)
    };

    // 8.143: re-notifica la diferencia formateada al cambiar el ajuste de moneda en caliente.
    public void NotifyCurrencyFormatChanged()
    {
        OnPropertyChanged(nameof(DifferenceDisplay));
    }
}

public partial class DailyClosureViewModel : ObservableObject, IDisposable
{
    private readonly IDailyClosureClientService _closureService;
    private readonly IDialogService _dialogService;
    private readonly IFilePickerDialog _filePicker;
    private bool _disposed;
    public UserSession? UserSession { get; }

    public bool CanToggleBlindClosing => UserSession?.IsAdmin == true;

    /// <summary>
    /// Residual 8.187-8.188: el arqueo USD esperado/diferencia solo se muestra a supervisores,
    /// igual que el esperado Bs.S (blind closing 8.153). El cajero conserva el conteo ciego.
    /// </summary>
    public bool CanViewExpectedUsd => UserSession?.IsAdmin == true || UserSession?.IsManager == true;

    public DailyClosureViewModel(IDailyClosureClientService closureService, IDialogService dialogService, UserSession? userSession = null, IFilePickerDialog? filePicker = null)
    {
        _closureService = closureService;
        _dialogService = dialogService;
        _filePicker = filePicker ?? new NoopFilePicker();
        UserSession = userSession;

        // Forced true for cashiers, default false for admins
        _isBlindClosing = UserSession?.IsCashier == true;

        // 8.143: refresco en vivo del formato de moneda para el cierre diario abierto.
        WeakReferenceMessenger.Default.Register<Desktop.Client.Messages.CurrencyFormatChangedMessage>(this, (r, m) =>
        {
            var vm = (DailyClosureViewModel)r;
            vm.OnPropertyChanged(nameof(DifferenceStatusLabel));
            vm.OnPropertyChanged(nameof(DifferenceStatusColor));
            foreach (var row in vm.DetailRows)
            {
                row.NotifyCurrencyFormatChanged();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
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
                OnPropertyChanged(nameof(CanViewUsdExpected));
                OnPropertyChanged(nameof(ShowUsdReconciliation));
                OnPropertyChanged(nameof(ExpectedClosingBalanceUsdDisplay));
                OnPropertyChanged(nameof(DifferenceUsdDisplay));
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

    /// <summary>
    /// Residual 8.187-8.188: conteo fisico USD opcional. Se guarda como texto para distinguir
    /// "vacio" (sin conteo, contrato previo) de "0.00" (conteo en cero), algo que un decimal no
    /// puede expresar con el binding actual de la vista.
    /// </summary>
    private string _actualClosingBalanceUsdText = string.Empty;
    public string ActualClosingBalanceUsdText
    {
        get => _actualClosingBalanceUsdText;
        set
        {
            if (SetProperty(ref _actualClosingBalanceUsdText, value))
            {
                OnPropertyChanged(nameof(HasUsdCountInput));
            }
        }
    }

    public bool HasUsdCountInput => !string.IsNullOrWhiteSpace(ActualClosingBalanceUsdText);

    /// <summary>
    /// Resultado del ultimo cierre creado. Es la unica fuente del esperado/diferencia USD: el
    /// servidor los calcula por bucket de la sesion y los oculta a roles no supervisores.
    /// </summary>
    private DailyClosureDto? _lastCreatedClosure;
    public DailyClosureDto? LastCreatedClosure
    {
        get => _lastCreatedClosure;
        private set
        {
            if (SetProperty(ref _lastCreatedClosure, value))
            {
                OnPropertyChanged(nameof(HasUsdClosureResult));
                OnPropertyChanged(nameof(ShowUsdReconciliation));
                OnPropertyChanged(nameof(ExpectedClosingBalanceUsdDisplay));
                OnPropertyChanged(nameof(DifferenceUsdDisplay));
            }
        }
    }

    public bool HasUsdClosureResult => LastCreatedClosure?.ActualClosingBalanceUsd.HasValue == true;

    /// <summary>Esperado/diferencia se muestran solo cuando el rol y el modo ciego lo permiten.</summary>
    public bool CanViewUsdExpected => CanViewExpectedUsd && !IsBlindClosing;

    public bool ShowUsdReconciliation => CanViewUsdExpected && HasUsdClosureResult;

    public string ExpectedClosingBalanceUsdDisplay => ShowUsdReconciliation
        && LastCreatedClosure?.ExpectedClosingBalanceUsd is { } expected
            ? PettyCashLabels.FormatUsd(expected)
            : "—";

    public string DifferenceUsdDisplay => ShowUsdReconciliation
        && LastCreatedClosure?.DifferenceUsd is { } difference
            ? (difference > 0 ? "+" : string.Empty) + PettyCashLabels.FormatUsd(difference)
            : "—";

    /// <summary>
    /// Interpreta el conteo USD: vacio = sin conteo (null), texto invalido o negativo = error de
    /// validacion. Reutiliza el parser tolerante del Core (es-VE/internacional).
    /// </summary>
    public bool TryGetActualClosingBalanceUsd(out decimal? amount, out string? error)
    {
        amount = null;
        error = null;

        if (string.IsNullOrWhiteSpace(ActualClosingBalanceUsdText))
        {
            return true;
        }

        if (!FlexibleDecimalParser.TryParse(ActualClosingBalanceUsdText, out decimal parsed))
        {
            error = "Ingrese un conteo USD válido o deje el campo vacío.";
            return false;
        }

        if (parsed < 0m)
        {
            error = "El conteo de efectivo USD no puede ser negativo.";
            return false;
        }

        amount = Math.Round(parsed, 4, MidpointRounding.AwayFromZero);
        return true;
    }

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
        ? $"SOBRANTE EN CAJA (+{CurrencyDisplay.Number(TotalDifferenceBsS)} Bs.S)"
        : (TotalDifferenceBsS < 0 ? $"FALTANTE EN CAJA ({CurrencyDisplay.Number(TotalDifferenceBsS)} Bs.S)" : "CUADRADO EXACTO");

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

    private string BuildConfirmationMessage(decimal? actualClosingBalanceUsd)
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
                sb.AppendLine($"• {row.PaymentMethodName}: {MoneyFormat.N2(row.ActualAmountBsS)} Bs.S");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"TOTAL DECLARADO: {MoneyFormat.N2(TotalActualBsS)} Bs.S");
            if (actualClosingBalanceUsd.HasValue)
            {
                sb.AppendLine($"EFECTIVO USD CONTADO: {PettyCashLabels.FormatUsd(actualClosingBalanceUsd.Value)}");
            }
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
                sb.AppendLine($"    Esperado:  {MoneyFormat.N2(row.ExpectedAmountBsS)} Bs.S");
                sb.AppendLine($"    Declarado: {MoneyFormat.N2(row.ActualAmountBsS)} Bs.S");
                sb.AppendLine($"    Diferencia: {diffSign}{MoneyFormat.N2(row.DifferenceBsS)} Bs.S");
            }
            sb.AppendLine("-----------------------------------");
            sb.AppendLine($"TOTAL ESPERADO:  {MoneyFormat.N2(TotalExpectedBsS)} Bs.S");
            sb.AppendLine($"TOTAL DECLARADO: {MoneyFormat.N2(TotalActualBsS)} Bs.S");
            var totalDiffSign = TotalDifferenceBsS >= 0 ? "+" : "";
            sb.AppendLine($"DIFERENCIA NETA: {totalDiffSign}{MoneyFormat.N2(TotalDifferenceBsS)} Bs.S ({DifferenceStatusLabel})");
            if (actualClosingBalanceUsd.HasValue)
            {
                sb.AppendLine($"EFECTIVO USD CONTADO: {PettyCashLabels.FormatUsd(actualClosingBalanceUsd.Value)}");
            }
            if (!string.IsNullOrWhiteSpace(Observation))
            {
                sb.AppendLine($"Observaciones: {Observation}");
            }
            sb.AppendLine("\n¿Desea confirmar y registrar este cierre contable?");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Mensaje de exito con el arqueo USD cuando el servidor lo devolvio y el rol puede verlo.
    /// El cajero conserva el cierre ciego (solo se le confirma que el cierre fue registrado).
    /// </summary>
    private string BuildSuccessMessage()
    {
        var message = "Cierre diario procesado y guardado exitosamente. Comprobantes guardados automáticamente en Descargas y en Documentos\\Registro de cierres.\n\nLos acumuladores de ingresos y egresos han sido reiniciados a 0.00 Bs.S para el nuevo turno.";

        if (!ShowUsdReconciliation || LastCreatedClosure is not { ActualClosingBalanceUsd: { } actual } closure)
        {
            return message;
        }

        var usd = new System.Text.StringBuilder();
        usd.AppendLine();
        usd.AppendLine();
        usd.AppendLine("ARQUEO FÍSICO USD:");
        usd.AppendLine($"  Contado:    {PettyCashLabels.FormatUsd(actual)}");
        if (closure.ExpectedClosingBalanceUsd is { } expected)
        {
            usd.AppendLine($"  Esperado:   {PettyCashLabels.FormatUsd(expected)}");
        }
        if (closure.DifferenceUsd is { } difference)
        {
            usd.AppendLine($"  Diferencia: {(difference > 0 ? "+" : string.Empty)}{PettyCashLabels.FormatUsd(difference)}");
        }

        return message + usd.ToString().TrimEnd();
    }

    [RelayCommand]
    private async Task ConfirmClosure()
    {
        if (!DetailRows.Any())
        {
            _dialogService.ShowWarning("Advertencia", "No hay datos de métodos de pago para procesar el cierre.");
            return;
        }

        if (!TryGetActualClosingBalanceUsd(out decimal? actualClosingBalanceUsd, out string? usdError))
        {
            _dialogService.ShowWarning("Advertencia", usdError ?? "Ingrese un conteo USD válido o deje el campo vacío.");
            return;
        }

        string confirmMessage = BuildConfirmationMessage(actualClosingBalanceUsd);
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
                ActualClosingBalanceUsd = actualClosingBalanceUsd,
                Details = DetailRows.Select(r => new Services.CreateClosureDetailRequest
                {
                    PaymentMethodId = r.PaymentMethodId,
                    PaymentMethodName = r.PaymentMethodName,
                    ExpectedAmountBsS = r.ExpectedAmountBsS,
                    ActualAmountBsS = r.ActualAmountBsS
                }).ToList()
            };

            LastCreatedClosure = await _closureService.CreateClosureAsync(request);
            IsSaved = true;
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new Desktop.Client.Messages.ShiftClosedMessage());
            _dialogService.ShowInfo("Éxito de Cierre", BuildSuccessMessage());
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
    private async Task ExportClosureReceiptAsync()
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
                sb.AppendLine($"  {row.PaymentMethodName,-22} Bs.S {MoneyFormat.N2(row.ActualAmountBsS),10}");
            }
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:         Bs.S {MoneyFormat.N2(TotalActualBsS),10}");
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
                sb.AppendLine($"    Declarado: Bs.S {MoneyFormat.N2(row.ActualAmountBsS)} | Esperado: Bs.S {MoneyFormat.N2(row.ExpectedAmountBsS)} | Dif: Bs.S {MoneyFormat.N2(row.DifferenceBsS)}");
            }
            sb.AppendLine("-----------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:  Bs.S {MoneyFormat.N2(TotalActualBsS),10}");
            sb.AppendLine($"TOTAL ESPERADO:   Bs.S {MoneyFormat.N2(TotalExpectedBsS),10}");
            sb.AppendLine($"DIFERENCIA TOTAL: Bs.S {MoneyFormat.N2(TotalDifferenceBsS),10}");
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
                await System.IO.File.WriteAllTextAsync(savePath, sb.ToString());
                _dialogService.ShowInfo("Comprobante Guardado", $"El comprobante se guardó correctamente en:\n{savePath}");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error de Exportación", $"No se pudo guardar el comprobante: {ex.Message}");
        }
    }
}
