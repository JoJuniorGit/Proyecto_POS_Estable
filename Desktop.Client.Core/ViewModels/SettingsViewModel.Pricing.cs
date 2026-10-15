using CommunityToolkit.Mvvm.ComponentModel;
using Core.Common;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.184-T4 (S2): "Políticas de Precios y Redondeo" — regla USD global (Detal/Mayor), regla de
/// efectivo Bs.S (proyección en caliente) y toggle de protección de piso de margen. Molde del
/// partial de formato de moneda: carga en <see cref="SettingsViewModel.EnsureLoadedAsync"/> con
/// fallback silencioso a los defaults del sistema (ExactMath / ExactMath / piso activo) y
/// auto-guardado de los TRES campos en cada cambio, con revert de la UI + error si el PUT falla.
/// </summary>
public partial class SettingsViewModel
{
    public class PricingRoundingOption
    {
        public RoundingRuleType Rule { get; set; } = RoundingRuleType.ExactMath;
        public string DisplayName { get; set; } = string.Empty;
    }

    public ObservableCollection<PricingRoundingOption> UsdRoundingOptions { get; } = new()
    {
        new PricingRoundingOption { Rule = RoundingRuleType.ExactMath, DisplayName = "Sin redondeo" },
        new PricingRoundingOption { Rule = RoundingRuleType.UsdPsychological, DisplayName = "Psicológico USD (.99/.95/.75/.50/.25)" }
    };

    public ObservableCollection<PricingRoundingOption> CashBsRoundingOptions { get; } = new()
    {
        new PricingRoundingOption { Rule = RoundingRuleType.ExactMath, DisplayName = "Sin redondeo" },
        new PricingRoundingOption { Rule = RoundingRuleType.VesCashFriendlyInteger, DisplayName = "Entero" },
        new PricingRoundingOption { Rule = RoundingRuleType.VesCashFriendlyStep5, DisplayName = "Múltiplo de 5" },
        new PricingRoundingOption { Rule = RoundingRuleType.VesCashFriendlyStep10, DisplayName = "Múltiplo de 10" }
    };

    private PricingRoundingOption? _selectedUsdRoundingRule;
    public PricingRoundingOption? SelectedUsdRoundingRule
    {
        get => _selectedUsdRoundingRule;
        set
        {
            if (SetProperty(ref _selectedUsdRoundingRule, value) && !_isApplyingPricingPolicies)
            {
                QueuePricingPoliciesSave();
            }
        }
    }

    private PricingRoundingOption? _selectedCashBsRoundingRule;
    public PricingRoundingOption? SelectedCashBsRoundingRule
    {
        get => _selectedCashBsRoundingRule;
        set
        {
            if (SetProperty(ref _selectedCashBsRoundingRule, value) && !_isApplyingPricingPolicies)
            {
                QueuePricingPoliciesSave();
            }
        }
    }

    [ObservableProperty]
    private bool _enforceMinimumMarginFloor = true;

    partial void OnEnforceMinimumMarginFloorChanged(bool value)
    {
        if (!_isApplyingPricingPolicies)
        {
            QueuePricingPoliciesSave();
        }
    }

    /// <summary>Tarea del último auto-guardado de políticas (awaitable en pruebas).</summary>
    public Task PricingPoliciesSaveTask { get; private set; } = Task.CompletedTask;

    /// <summary>Suprime auto-guardados durante la aplicación programática (carga y revert).</summary>
    private bool _isApplyingPricingPolicies;

    /// <summary>Último estado aplicado con éxito (o cargado): destino del revert ante fallo de PUT.</summary>
    private PricingPoliciesDto _appliedPricingPolicies = new();

    private async Task LoadPricingPoliciesAsync()
    {
        PricingPoliciesDto policies;
        try
        {
            policies = await _settingsService.GetPricingPoliciesAsync() ?? new PricingPoliciesDto();
        }
        catch
        {
            // Fallback silencioso: rigen los defaults del sistema (ExactMath / ExactMath / piso activo).
            policies = new PricingPoliciesDto();
        }

        ApplyPricingPolicies(policies);
    }

    private void QueuePricingPoliciesSave()
    {
        PricingPoliciesSaveTask = SavePricingPoliciesAsync();
        PricingPoliciesSaveTask.SafeFireAndForget("Settings.PricingPoliciesChanged");
    }

    private async Task SavePricingPoliciesAsync()
    {
        // El PUT reemplaza el bloque completo: los tres valores viajan juntos en cada cambio.
        var requested = new PricingPoliciesDto
        {
            UsdRoundingRule = SelectedUsdRoundingRule?.Rule ?? RoundingRuleType.ExactMath,
            CashBsRoundingRule = SelectedCashBsRoundingRule?.Rule ?? RoundingRuleType.ExactMath,
            EnforceMinimumMarginFloor = EnforceMinimumMarginFloor
        };

        try
        {
            await _settingsService.SetPricingPoliciesAsync(requested);
            _appliedPricingPolicies = requested;
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al guardar las políticas de precios y redondeo: {ex.Message}",
                nameof(SettingsViewModel));

            // Revert al último estado aplicado: solo si la UI sigue mostrando lo que se intentó
            // guardar (un cambio posterior no debe ser pisado por el fallo de un guardado viejo).
            if (IsCurrentSelection(requested))
            {
                ApplyPricingPolicies(_appliedPricingPolicies);
            }

            _dialogService?.ShowError(
                "Políticas de Precios y Redondeo",
                "No se pudieron guardar las políticas. Se restauró la última configuración vigente.");
        }
    }

    private bool IsCurrentSelection(PricingPoliciesDto policies) =>
        (SelectedUsdRoundingRule?.Rule ?? RoundingRuleType.ExactMath) == policies.UsdRoundingRule
        && (SelectedCashBsRoundingRule?.Rule ?? RoundingRuleType.ExactMath) == policies.CashBsRoundingRule
        && EnforceMinimumMarginFloor == policies.EnforceMinimumMarginFloor;

    private void ApplyPricingPolicies(PricingPoliciesDto policies)
    {
        _isApplyingPricingPolicies = true;
        try
        {
            SelectedUsdRoundingRule = FindOption(UsdRoundingOptions, policies.UsdRoundingRule);
            SelectedCashBsRoundingRule = FindOption(CashBsRoundingOptions, policies.CashBsRoundingRule);
            EnforceMinimumMarginFloor = policies.EnforceMinimumMarginFloor;
            _appliedPricingPolicies = policies;
        }
        finally
        {
            _isApplyingPricingPolicies = false;
        }
    }

    /// <summary>Valor ilegible o fuera del contrato (muralla del lector) cae a la primera opción (ExactMath).</summary>
    private static PricingRoundingOption FindOption(IEnumerable<PricingRoundingOption> options, RoundingRuleType rule)
        => options.FirstOrDefault(option => option.Rule == rule) ?? options.First();
}
