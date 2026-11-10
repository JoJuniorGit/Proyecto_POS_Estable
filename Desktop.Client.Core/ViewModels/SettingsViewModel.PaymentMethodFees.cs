using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// F4-WU1 (Paso 14): administración de reglas de comisión de medios de pago dentro de
/// "Métodos de Pago y Caja". El endpoint F3 es Admin-only, por lo que la tarjeta y las acciones
/// quedan ocultas/bloqueadas para Manager y Cashier. La comisión es costo del negocio y nunca se
/// agrega al checkout ni al cliente.
/// </summary>
public partial class SettingsViewModel
{
    public sealed class PaymentMethodFeeTypeOption
    {
        public PaymentMethodFeeTypeClient Value { get; init; }

        public string DisplayName { get; init; } = string.Empty;
    }

    public ObservableCollection<PaymentMethodFeeRuleClientDto> PaymentMethodFeeRules { get; } = new();

    public ObservableCollection<PaymentMethodDto> FeeEligiblePaymentMethods { get; } = new();

    public ObservableCollection<PaymentMethodFeeTypeOption> FeeTypeOptions { get; } = new()
    {
        new PaymentMethodFeeTypeOption
        {
            Value = PaymentMethodFeeTypeClient.Percentage,
            DisplayName = "Porcentaje (%)"
        },
        new PaymentMethodFeeTypeOption
        {
            Value = PaymentMethodFeeTypeClient.Fixed,
            DisplayName = "Monto fijo"
        },
        new PaymentMethodFeeTypeOption
        {
            Value = PaymentMethodFeeTypeClient.PercentagePlusFixed,
            DisplayName = "Porcentaje + fijo"
        }
    };

    public ObservableCollection<string> FeeCurrencyOptions { get; } = new();

    [ObservableProperty]
    private bool _isFeeRuleLoading;

    [ObservableProperty]
    private bool _isFeeRuleSaving;

    [ObservableProperty]
    private bool _isFeeRuleFormVisible;

    [ObservableProperty]
    private string _feeRuleStatus = string.Empty;

    [ObservableProperty]
    private int? _editingFeeRuleId;

    [ObservableProperty]
    private PaymentMethodDto? _selectedFeePaymentMethod;

    [ObservableProperty]
    private PaymentMethodFeeTypeOption? _selectedFeeTypeOption;

    [ObservableProperty]
    private string _feePercentageText = string.Empty;

    [ObservableProperty]
    private string _feeFixedAmountText = string.Empty;

    [ObservableProperty]
    private string _feeCurrencyCode = "USD";

    [ObservableProperty]
    private DateTime? _feeEffectiveFromDate = DateTime.Today;

    [ObservableProperty]
    private DateTime? _feeEffectiveToDate;

    [ObservableProperty]
    private bool _isFeeRuleActive = true;

    public bool CanEditPaymentMethodFeeRules => UserSession == null || UserSession.IsAdmin;

    public bool IsNewFeeRule => !EditingFeeRuleId.HasValue;

    public bool CanSaveFeeRule => CanEditPaymentMethodFeeRules && !IsFeeRuleSaving;

    public bool HasFeeRuleStatus => !string.IsNullOrWhiteSpace(FeeRuleStatus);

    public bool ShowFeePercentage => SelectedFeeTypeOption?.Value != PaymentMethodFeeTypeClient.Fixed;

    public bool ShowFeeFixedAmount =>
        SelectedFeeTypeOption?.Value is PaymentMethodFeeTypeClient.Fixed
            or PaymentMethodFeeTypeClient.PercentagePlusFixed;

    partial void OnEditingFeeRuleIdChanged(int? value)
        => OnPropertyChanged(nameof(IsNewFeeRule));

    partial void OnIsFeeRuleSavingChanged(bool value)
        => OnPropertyChanged(nameof(CanSaveFeeRule));

    partial void OnFeeRuleStatusChanged(string value)
        => OnPropertyChanged(nameof(HasFeeRuleStatus));

    partial void OnSelectedFeeTypeOptionChanged(PaymentMethodFeeTypeOption? value)
    {
        OnPropertyChanged(nameof(ShowFeePercentage));
        OnPropertyChanged(nameof(ShowFeeFixedAmount));
    }

    [RelayCommand]
    public async Task LoadPaymentMethodFeeRulesAsync()
    {
        SyncFeeEligiblePaymentMethods();
        RefreshFeeCurrencyOptions();

        if (!CanEditPaymentMethodFeeRules)
        {
            PaymentMethodFeeRules.Clear();
            return;
        }

        if (_profitabilityService == null)
        {
            return;
        }

        IsFeeRuleLoading = true;
        FeeRuleStatus = string.Empty;
        try
        {
            var rules = await _profitabilityService.GetFeeRulesAsync(
                paymentMethodId: null,
                activeOnly: null);
            PaymentMethodFeeRules.Clear();
            foreach (var rule in rules)
            {
                PaymentMethodFeeRules.Add(rule);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al cargar las reglas de comisión: {ex.Message}",
                nameof(SettingsViewModel));
            FeeRuleStatus = $"No se pudieron cargar las reglas de comisión: {ex.Message}";
        }
        finally
        {
            IsFeeRuleLoading = false;
        }
    }

    [RelayCommand]
    private void NewFeeRule()
    {
        if (!CanEditPaymentMethodFeeRules)
        {
            return;
        }

        EditingFeeRuleId = null;
        SelectedFeePaymentMethod = FeeEligiblePaymentMethods.FirstOrDefault();
        SelectedFeeTypeOption = FeeTypeOptions[0];
        FeePercentageText = string.Empty;
        FeeFixedAmountText = string.Empty;
        FeeCurrencyCode = FeeCurrencyOptions.FirstOrDefault() ?? "USD";
        FeeEffectiveFromDate = DateTime.Today;
        FeeEffectiveToDate = null;
        IsFeeRuleActive = true;
        FeeRuleStatus = string.Empty;
        IsFeeRuleFormVisible = true;
    }

    [RelayCommand]
    private void EditFeeRule(PaymentMethodFeeRuleClientDto? rule)
    {
        if (!CanEditPaymentMethodFeeRules || rule == null)
        {
            return;
        }

        EditingFeeRuleId = rule.Id;
        var method = FeeEligiblePaymentMethods.FirstOrDefault(candidate => candidate.Id == rule.PaymentMethodId);
        if (method == null)
        {
            method = new PaymentMethodDto
            {
                Id = rule.PaymentMethodId,
                Name = rule.PaymentMethodName,
                IsCash = false
            };
            FeeEligiblePaymentMethods.Add(method);
        }

        SelectedFeePaymentMethod = method;
        SelectedFeeTypeOption = FeeTypeOptions.FirstOrDefault(option => option.Value == rule.FeeType)
            ?? FeeTypeOptions[0];
        FeePercentageText = FormatFeeDecimal(rule.Percentage);
        FeeFixedAmountText = FormatFeeDecimal(rule.FixedAmount);
        FeeCurrencyCode = string.IsNullOrWhiteSpace(rule.CurrencyCode) ? "USD" : rule.CurrencyCode;
        // El backend trata las fechas de vigencia como UTC; el DatePicker conserva el día
        // almacenado para que editar y volver a guardar no desplace la regla un día.
        FeeEffectiveFromDate = rule.EffectiveFromUtc.Date;
        FeeEffectiveToDate = rule.EffectiveToUtc?.Date;
        IsFeeRuleActive = rule.IsActive;
        FeeRuleStatus = string.Empty;
        IsFeeRuleFormVisible = true;
    }

    [RelayCommand]
    private void CancelFeeRuleEdit()
    {
        IsFeeRuleFormVisible = false;
        FeeRuleStatus = string.Empty;
    }

    [RelayCommand]
    private async Task SaveFeeRuleAsync()
    {
        if (!CanEditPaymentMethodFeeRules || IsFeeRuleSaving)
        {
            return;
        }

        if (_profitabilityService == null)
        {
            FeeRuleStatus = "El servicio de reglas de comisión no está disponible.";
            return;
        }

        if (!TryBuildFeeValues(
                out var feeType,
                out var percentage,
                out var fixedAmount,
                out var effectiveFromUtc,
                out var effectiveToUtc,
                out var error))
        {
            FeeRuleStatus = error;
            return;
        }

        var isUpdate = EditingFeeRuleId.HasValue;
        if (!isUpdate && SelectedFeePaymentMethod == null)
        {
            FeeRuleStatus = "Seleccione un método de pago para la regla de comisión.";
            return;
        }

        IsFeeRuleSaving = true;
        FeeRuleStatus = string.Empty;
        try
        {
            PaymentMethodFeeRuleClientDto saved;
            if (isUpdate)
            {
                saved = await _profitabilityService!.UpdateFeeRuleAsync(
                    EditingFeeRuleId!.Value,
                    new UpdatePaymentMethodFeeRuleClientRequest
                    {
                        FeeType = feeType,
                        Percentage = percentage,
                        FixedAmount = fixedAmount,
                        CurrencyCode = FeeCurrencyCode.Trim(),
                        EffectiveFromUtc = effectiveFromUtc,
                        EffectiveToUtc = effectiveToUtc,
                        IsActive = IsFeeRuleActive
                    });
            }
            else
            {
                saved = await _profitabilityService!.CreateFeeRuleAsync(
                    new CreatePaymentMethodFeeRuleClientRequest
                    {
                        PaymentMethodId = SelectedFeePaymentMethod!.Id,
                        FeeType = feeType,
                        Percentage = percentage,
                        FixedAmount = fixedAmount,
                        CurrencyCode = FeeCurrencyCode.Trim(),
                        EffectiveFromUtc = effectiveFromUtc,
                        EffectiveToUtc = effectiveToUtc,
                        IsActive = IsFeeRuleActive
                    });
            }

            ReplaceFeeRule(saved);
            IsFeeRuleFormVisible = false;
            FeeRuleStatus = isUpdate
                ? "Regla de comisión actualizada correctamente."
                : "Regla de comisión guardada correctamente.";
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al guardar la regla de comisión: {ex.Message}",
                nameof(SettingsViewModel));
            FeeRuleStatus = $"No se pudo guardar la regla de comisión: {ex.Message}";
            _dialogService?.ShowError(
                "Reglas de comisión",
                "No se pudo guardar la regla de comisión. Verifique los valores e intente nuevamente.");
        }
        finally
        {
            IsFeeRuleSaving = false;
        }
    }

    [RelayCommand]
    private async Task DeleteFeeRuleAsync(PaymentMethodFeeRuleClientDto? rule)
    {
        if (!CanEditPaymentMethodFeeRules || rule == null)
        {
            return;
        }

        if (_profitabilityService == null)
        {
            FeeRuleStatus = "El servicio de reglas de comisión no está disponible.";
            return;
        }

        var confirmed = _dialogService?.ShowConfirm(
            "Desactivar regla de comisión",
            $"¿Está seguro de desactivar la regla de comisión de '{rule.PaymentMethodName}'?\n\nLa regla quedará inactiva; los cobros históricos conservan su comisión registrada.") == true;
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _profitabilityService!.DeleteFeeRuleAsync(rule.Id);
            rule.IsActive = false;
            FeeRuleStatus = "Regla de comisión desactivada correctamente.";
            OnPropertyChanged(nameof(PaymentMethodFeeRules));
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al desactivar la regla de comisión: {ex.Message}",
                nameof(SettingsViewModel));
            FeeRuleStatus = $"No se pudo desactivar la regla de comisión: {ex.Message}";
            _dialogService?.ShowError(
                "Reglas de comisión",
                "No se pudo desactivar la regla de comisión. Intente nuevamente.");
        }
    }

}
