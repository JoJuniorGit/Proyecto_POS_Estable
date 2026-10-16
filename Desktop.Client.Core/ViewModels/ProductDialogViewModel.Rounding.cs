using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Core.Helpers;
using Core.Logging;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.184-T3 (S4): opción de un selector de redondeo por artículo. <c>Rule</c> null = "Heredar
/// global": el artículo no persiste override y la política del sistema resuelve la regla efectiva.
/// </summary>
public sealed record RoundingRuleOption(RoundingRuleType? Rule, string Label);

/// <summary>
/// 8.184-T3 (S4): redondeo inteligente del modal de producto. Dos selectores por artículo
/// (Redondeo USD y Efectivo Bs.S) precargados desde la política global con opción "Heredar global"
/// (null) y override explícito; el cálculo vive dentro de <c>CalculatePricing</c>:
/// - Regla USD efectiva (artículo ?? global): redondea el precio final de cada canal con el piso de
///   margen objetivo del canal y el toggle global de protección de margen (ExactMath conserva el
///   comportamiento previo byte-idéntico).
/// - Regla de efectivo efectiva: proyecta EN CALIENTE el Bs.S de efectivo con la tasa viva a
///   través del helper único del cliente 8.185-T4 (`EffectivePriceVesHelper`, regla geométrica A
///   con el toggle global): nunca se persiste; el Bs.S almacenado sigue siendo el canónico 8.104.
///   Solo el precio efectivo mostrado se unifica; las trazas de margen costo-aware 8.184 (Δ
///   Redondeo / Margen Efectivo %) se conservan como información.
/// El margen persistido es el objetivo/operado (regla 8.183): solo el gatillo manual re-deriva el
/// margen del precio final redondeado para mantener la coherencia del diálogo.
/// </summary>
public partial class ProductDialogViewModel
{
    private const string ExactMathOptionLabel = "Sin redondeo";
    private const string PsychologicalOptionLabel = "Psicológico USD (.99/.95/.75/.50/.25)";
    private const string CashIntegerOptionLabel = "Entero";
    private const string CashStep5OptionLabel = "Múltiplo de 5";
    private const string CashStep10OptionLabel = "Múltiplo de 10";

    private readonly ISettingsService? _settingsService;

    // Defaults globales vigentes hasta que la política llegue del server (o si no hay servicio):
    // ExactMath / ExactMath / piso activo (S1). Con ExactMath el cálculo previo queda intacto.
    private RoundingRuleType _globalUsdRoundingRule = RoundingRuleType.ExactMath;
    private RoundingRuleType _globalCashBsRoundingRule = RoundingRuleType.ExactMath;
    private bool _globalEnforceMinimumMarginFloor = true;
    private bool _suppressRoundingRecalculate;

    // Trazas del último cálculo por canal (USD, unidad de la regla USD) y proyección de efectivo
    // en caliente (Bs.S, nunca persistida).
    private decimal? _retailMathBase;
    private decimal? _retailRoundingDelta;
    private decimal? _retailEffectiveMarginPct;
    private decimal? _wholesaleMathBase;
    private decimal? _wholesaleRoundingDelta;
    private decimal? _wholesaleEffectiveMarginPct;
    private decimal? _retailCashFriendlyBsS;
    private decimal? _wholesaleCashFriendlyBsS;
    private string _roundingValidationMessage = string.Empty;

    public ObservableCollection<RoundingRuleOption> RoundingUsdOptions { get; } = new()
    {
        new RoundingRuleOption(null, BuildInheritLabel(RoundingRuleType.ExactMath)),
        new RoundingRuleOption(RoundingRuleType.ExactMath, ExactMathOptionLabel),
        new RoundingRuleOption(RoundingRuleType.UsdPsychological, PsychologicalOptionLabel),
    };

    public ObservableCollection<RoundingRuleOption> RoundingCashOptions { get; } = new()
    {
        new RoundingRuleOption(null, BuildInheritLabel(RoundingRuleType.ExactMath)),
        new RoundingRuleOption(RoundingRuleType.ExactMath, ExactMathOptionLabel),
        new RoundingRuleOption(RoundingRuleType.VesCashFriendlyInteger, CashIntegerOptionLabel),
        new RoundingRuleOption(RoundingRuleType.VesCashFriendlyStep5, CashStep5OptionLabel),
        new RoundingRuleOption(RoundingRuleType.VesCashFriendlyStep10, CashStep10OptionLabel),
    };

    [ObservableProperty]
    private RoundingRuleOption? _selectedRoundingUsdOption;

    [ObservableProperty]
    private RoundingRuleOption? _selectedRoundingCashOption;

    /// <summary>Override USD del artículo (null = heredar global), tal como viaja en el guardado.</summary>
    public RoundingRuleType? SelectedRoundingRuleUsd => SelectedRoundingUsdOption?.Rule;

    /// <summary>Override de efectivo Bs.S del artículo (null = heredar global).</summary>
    public RoundingRuleType? SelectedRoundingRuleCashBs => SelectedRoundingCashOption?.Rule;

    /// <summary>Regla USD efectiva del cálculo: override del artículo ?? política global.</summary>
    public RoundingRuleType EffectiveUsdRoundingRule => SelectedRoundingUsdOption?.Rule ?? _globalUsdRoundingRule;

    /// <summary>Regla de efectivo Bs.S efectiva de la proyección: override ?? política global.</summary>
    public RoundingRuleType EffectiveCashBsRoundingRule => SelectedRoundingCashOption?.Rule ?? _globalCashBsRoundingRule;

    // --- Display por canal (las etiquetas USD se muestran solo con regla efectiva ≠ ExactMath) ---

    public bool ShowRetailRoundingTrace => EffectiveUsdRoundingRule != RoundingRuleType.ExactMath && PriceRetailUSD > 0m;
    public decimal? RetailMathBaseValue => _retailMathBase;
    public decimal? RetailRoundingDeltaValue => _retailRoundingDelta;
    public decimal? RetailEffectiveMarginPct => _retailEffectiveMarginPct;
    public string RetailMathBaseText => FormatUsd(_retailMathBase);
    public string RetailRoundingDeltaText => FormatSignedUsd(_retailRoundingDelta);
    public string RetailEffectiveMarginText => FormatMarginPct(_retailEffectiveMarginPct);

    public bool ShowRetailCashFriendly => _retailCashFriendlyBsS.HasValue;
    public decimal? RetailCashFriendlyBsS => _retailCashFriendlyBsS;
    public string RetailCashFriendlyText => _retailCashFriendlyBsS.HasValue ? FormatBsS(_retailCashFriendlyBsS.Value) : string.Empty;

    public bool ShowWholesaleRoundingTrace => EffectiveUsdRoundingRule != RoundingRuleType.ExactMath && HasWholesale && PriceWholesaleUSD > 0m;
    public decimal? WholesaleMathBaseValue => _wholesaleMathBase;
    public decimal? WholesaleRoundingDeltaValue => _wholesaleRoundingDelta;
    public decimal? WholesaleEffectiveMarginPct => _wholesaleEffectiveMarginPct;
    public string WholesaleMathBaseText => FormatUsd(_wholesaleMathBase);
    public string WholesaleRoundingDeltaText => FormatSignedUsd(_wholesaleRoundingDelta);
    public string WholesaleEffectiveMarginText => FormatMarginPct(_wholesaleEffectiveMarginPct);

    public bool ShowWholesaleCashFriendly => _wholesaleCashFriendlyBsS.HasValue;
    public decimal? WholesaleCashFriendlyBsS => _wholesaleCashFriendlyBsS;
    public string WholesaleCashFriendlyText => _wholesaleCashFriendlyBsS.HasValue ? FormatBsS(_wholesaleCashFriendlyBsS.Value) : string.Empty;

    /// <summary>Aviso vivo Detal/Mayor tras redondear; informativo (el server sigue siendo la autoridad 400).</summary>
    public bool HasRoundingValidationWarning => !string.IsNullOrEmpty(_roundingValidationMessage);
    public string RoundingValidationMessage => _roundingValidationMessage;

    partial void OnSelectedRoundingUsdOptionChanged(RoundingRuleOption? value) => OnRoundingSelectionChanged();

    partial void OnSelectedRoundingCashOptionChanged(RoundingRuleOption? value) => OnRoundingSelectionChanged();

    private void OnRoundingSelectionChanged()
    {
        if (_suppressRoundingRecalculate)
        {
            return;
        }

        CalculatePricing("Cost");
    }

    /// <summary>
    /// Pre-selección desde el artículo editado: override persistido o "Heredar global" (null). Se
    /// llama en el constructor antes del primer <c>CalculatePricing</c>, sin recalcular.
    /// </summary>
    private void InitializeRoundingSelection()
    {
        _suppressRoundingRecalculate = true;
        try
        {
            SelectedRoundingUsdOption = FindRoundingOption(RoundingUsdOptions, _initialProduct?.RoundingRuleUsd);
            SelectedRoundingCashOption = FindRoundingOption(RoundingCashOptions, _initialProduct?.RoundingRuleCashBs);
        }
        finally
        {
            _suppressRoundingRecalculate = false;
        }
    }

    /// <summary>
    /// Carga la política global (fallback silencioso a los defaults ya vigentes) y recalcula para
    /// que el precio mostrado refleje la regla efectiva. Se dispara con la carga de metadatos del
    /// diálogo (molde categorías: lectura al abrir, sin SignalR v1).
    /// </summary>
    public async Task LoadPricingPoliciesAsync()
    {
        var client = _settingsService;
        if (client is null)
        {
            return;
        }

        try
        {
            var policies = await client.GetPricingPoliciesAsync() ?? new PricingPoliciesDto();

            void Apply() => ApplyPricingPolicies(policies);

            if (_dispatcherInvoker.CheckAccess())
            {
                Apply();
            }
            else
            {
                _dispatcherInvoker.Invoke(Apply);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError(
                $"Fallo al cargar las políticas globales de precios y redondeo: {ex.Message}",
                nameof(ProductDialogViewModel));
        }
    }

    private void ApplyPricingPolicies(PricingPoliciesDto policies)
    {
        _globalUsdRoundingRule = policies.UsdRoundingRule;
        _globalCashBsRoundingRule = policies.CashBsRoundingRule;
        _globalEnforceMinimumMarginFloor = policies.EnforceMinimumMarginFloor;

        _suppressRoundingRecalculate = true;
        try
        {
            // "Heredar global (X)" muestra la regla vigente; si el artículo heredaba, se re-selecciona
            // la opción nueva para que el combo no quede apuntando al objeto reemplazado.
            RoundingUsdOptions[0] = new RoundingRuleOption(null, BuildInheritLabel(_globalUsdRoundingRule));
            RoundingCashOptions[0] = new RoundingRuleOption(null, BuildInheritLabel(_globalCashBsRoundingRule));
            if (SelectedRoundingUsdOption?.Rule is null)
            {
                SelectedRoundingUsdOption = RoundingUsdOptions[0];
            }
            if (SelectedRoundingCashOption?.Rule is null)
            {
                SelectedRoundingCashOption = RoundingCashOptions[0];
            }
        }
        finally
        {
            _suppressRoundingRecalculate = false;
        }

        CalculatePricing("Cost");
    }

    private static RoundingRuleOption FindRoundingOption(IEnumerable<RoundingRuleOption> options, RoundingRuleType? rule)
        => options.FirstOrDefault(option => option.Rule == rule) ?? options.First();

    private static string BuildInheritLabel(RoundingRuleType globalRule)
        => $"Heredar global ({RoundingRuleLabel(globalRule)})";

    private static string RoundingRuleLabel(RoundingRuleType rule) => rule switch
    {
        RoundingRuleType.UsdPsychological => PsychologicalOptionLabel,
        RoundingRuleType.VesCashFriendlyInteger => CashIntegerOptionLabel,
        RoundingRuleType.VesCashFriendlyStep5 => CashStep5OptionLabel,
        RoundingRuleType.VesCashFriendlyStep10 => CashStep10OptionLabel,
        _ => ExactMathOptionLabel,
    };

    // -----------------------------------------------------------------------------------------
    // Cálculo vivo: se invoca al final de CalculatePricing, tras la matemática existente.
    // -----------------------------------------------------------------------------------------

    private void ApplyRoundingRules(string trigger)
    {
        var usdRule = EffectiveUsdRoundingRule;
        bool retailMoved = false;
        bool wholesaleMoved = false;

        if (usdRule == RoundingRuleType.ExactMath || PriceRetailUSD <= 0m)
        {
            ClearRetailRoundingTrace();
        }
        else
        {
            var result = PricingRoundingPolicy.Round(
                PriceRetailUSD, usdRule, CostPriceUSD, ProfitMarginRetail, ShouldEnforceFloor(ProfitMarginRetail));

            if (result.RoundedValue != PriceRetailUSD)
            {
                PriceRetailUSD = result.RoundedValue;
                retailMoved = true;
            }

            _retailMathBase = result.MathematicalValue;
            _retailRoundingDelta = result.RoundingDelta;
            _retailEffectiveMarginPct = result.EffectiveMarginPct;

            // Gatillo manual: el margen se re-deriva del precio FINAL redondeado para mantener la
            // coherencia in-dialog; en gatillos de costo/margen el margen objetivo se conserva como
            // input (regla 8.183: el redondeo nunca pisa el margen persistido).
            if (trigger == "PriceRetail" && result.RoundedValue != result.MathematicalValue)
            {
                ReDeriveRetailMarginFromRoundedPrice();
            }
        }

        if (usdRule == RoundingRuleType.ExactMath || !HasWholesale || PriceWholesaleUSD <= 0m)
        {
            ClearWholesaleRoundingTrace();
        }
        else
        {
            var result = PricingRoundingPolicy.Round(
                PriceWholesaleUSD, usdRule, CostPriceUSD, ProfitMarginWholesale, ShouldEnforceFloor(ProfitMarginWholesale));

            if (result.RoundedValue != PriceWholesaleUSD)
            {
                PriceWholesaleUSD = result.RoundedValue;
                wholesaleMoved = true;
            }

            _wholesaleMathBase = result.MathematicalValue;
            _wholesaleRoundingDelta = result.RoundingDelta;
            _wholesaleEffectiveMarginPct = result.EffectiveMarginPct;

            if (trigger == "PriceWholesale" && result.RoundedValue != result.MathematicalValue)
            {
                ReDeriveWholesaleMarginFromRoundedPrice();
            }
        }

        UpdateWholesaleCollisionGuard(usdRule, retailMoved, wholesaleMoved);

        if (usdRule != RoundingRuleType.ExactMath)
        {
            RefreshDerivedPricingFieldsAfterRounding();
        }

        UpdateCashFriendlyProjections();
        NotifyRoundingDisplayChanged();
    }

    /// <summary>
    /// Salvaguarda Detal/Mayor sobre redondeados (molde 8.182 S3 del bulk): si el Mayor quedó ≥ al
    /// Detal tras redondear, baja al escalón inmediato inferior de su regla siempre que siga por
    /// encima del costo; si no es posible, deja un aviso visible. El server sigue siendo la
    /// autoridad final (400) — el aviso es informativo y no bloquea el guardado.
    /// </summary>
    private void UpdateWholesaleCollisionGuard(RoundingRuleType usdRule, bool retailMoved, bool wholesaleMoved)
    {
        if (usdRule == RoundingRuleType.ExactMath || !HasWholesale || !(retailMoved || wholesaleMoved)
            || PriceRetailUSD <= 0m || PriceWholesaleUSD <= 0m || PriceWholesaleUSD < PriceRetailUSD)
        {
            SetRoundingValidationWarning(string.Empty);
            return;
        }

        decimal wholesaleBeforeSalvage = PriceWholesaleUSD;
        decimal? lowered = PricingRoundingPolicy.FindLowerStep(wholesaleBeforeSalvage, usdRule);
        if (lowered is > 0m && (CostPriceUSD <= 0m || lowered.Value > CostPriceUSD))
        {
            PriceWholesaleUSD = lowered.Value;

            // El ajuste de la salvaguarda también se reporta como delta del canal (molde bulk).
            if (_wholesaleMathBase.HasValue)
            {
                _wholesaleRoundingDelta = PriceWholesaleUSD - _wholesaleMathBase.Value;
            }

            _wholesaleEffectiveMarginPct = CostPriceUSD > 0m
                ? PricingCalculator.RoundToDigital((PriceWholesaleUSD - CostPriceUSD) / CostPriceUSD * 100m)
                : null;
            SetRoundingValidationWarning(string.Empty);
            return;
        }

        SetRoundingValidationWarning(
            $"El precio al mayor resultante (${wholesaleBeforeSalvage:F2}) no puede ser mayor o igual al detal (${PriceRetailUSD:F2}) ni pudo ajustarse al escalón inferior sin bajar del costo (${CostPriceUSD:F2}).");
    }

    /// <summary>
    /// Re-deriva los campos dependientes del precio final cuando el redondeo movió algún canal:
    /// Bs.S canónico (8.104), espejos Price/Cost/ProfitPercentage y bandera de venta con pérdida.
    /// Con ExactMath no se ejecuta (comportamiento previo intacto).
    /// </summary>
    private void RefreshDerivedPricingFieldsAfterRounding()
    {
        Price = PriceRetailUSD;
        Cost = CostPriceUSD;
        ProfitPercentage = ProfitMarginRetail;

        decimal rate = _exchangeRateService.CurrentRate;
        PriceRetailBsS = Desktop.Client.Helpers.PricingHelper.ToBsSCeiling(PriceRetailUSD, rate);
        PriceWholesaleBsS = Desktop.Client.Helpers.PricingHelper.ToBsSCeiling(PriceWholesaleUSD, rate);
        PriceBsS = PriceRetailBsS;

        IsSellingAtLoss = PriceRetailUSD > 0m && PriceRetailUSD < CostPriceUSD;
    }

    /// <summary>Misma fórmula del gatillo manual existente, anclada al precio FINAL redondeado.</summary>
    private void ReDeriveRetailMarginFromRoundedPrice()
    {
        if (CostPriceUSD > 0m)
        {
            decimal calculatedProfit = ((PriceRetailUSD / CostPriceUSD) - 1m) * 100m;
            ProfitMarginRetail = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            ProfitMarginRetail = 100m;
        }
    }

    private void ReDeriveWholesaleMarginFromRoundedPrice()
    {
        if (CostPriceUSD > 0m)
        {
            decimal calculatedProfit = ((PriceWholesaleUSD / CostPriceUSD) - 1m) * 100m;
            ProfitMarginWholesale = calculatedProfit < 0 ? 0 : Math.Round(calculatedProfit, 2, MidpointRounding.AwayFromZero);
        }
        else
        {
            ProfitMarginWholesale = 100m;
        }
    }

    /// <summary>Toggle global de protección + piso/costo válidos (misma guarda interna de la política).</summary>
    private bool ShouldEnforceFloor(decimal channelMarginPct)
        => _globalEnforceMinimumMarginFloor && channelMarginPct > 0m && CostPriceUSD > 0m;

    /// <summary>
    /// 8.183 (R1) + 8.185-T4 (S1): proyección en caliente del Bs.S de efectivo por canal con la
    /// regla cash efectiva y la tasa viva; nunca se persiste (el Bs.S almacenado es el canónico
    /// 8.104). El valor sale del helper ÚNICO del cliente (regla geométrica A con el toggle global)
    /// para coincidir 1:1 con Catálogo/POS/Carrito. Las trazas costo-aware 8.184 (Δ Redondeo /
    /// Margen Efectivo %) no se tocan.
    /// </summary>
    private void UpdateCashFriendlyProjections()
    {
        var cashRule = EffectiveCashBsRoundingRule;
        if (cashRule == RoundingRuleType.ExactMath)
        {
            _retailCashFriendlyBsS = null;
            _wholesaleCashFriendlyBsS = null;
            return;
        }

        // Sin tasa convertible la proyección queda oculta (mismo comportamiento previo).
        decimal rate = _exchangeRateService.CurrentRate;
        if (rate <= 0m)
        {
            _retailCashFriendlyBsS = null;
            _wholesaleCashFriendlyBsS = null;
            return;
        }

        bool neverRoundDown = _globalEnforceMinimumMarginFloor;
        _retailCashFriendlyBsS = PriceRetailUSD > 0m
            ? Desktop.Client.Helpers.EffectivePriceVesHelper.EffectiveBsS(PriceRetailUSD, rate, cashRule, neverRoundDown)
            : null;

        _wholesaleCashFriendlyBsS = HasWholesale && PriceWholesaleUSD > 0m
            ? Desktop.Client.Helpers.EffectivePriceVesHelper.EffectiveBsS(PriceWholesaleUSD, rate, cashRule, neverRoundDown)
            : null;
    }

    private void ClearRetailRoundingTrace()
    {
        _retailMathBase = null;
        _retailRoundingDelta = null;
        _retailEffectiveMarginPct = null;
    }

    private void ClearWholesaleRoundingTrace()
    {
        _wholesaleMathBase = null;
        _wholesaleRoundingDelta = null;
        _wholesaleEffectiveMarginPct = null;
    }

    private void SetRoundingValidationWarning(string message)
    {
        if (_roundingValidationMessage == message)
        {
            return;
        }

        _roundingValidationMessage = message;
        OnPropertyChanged(nameof(RoundingValidationMessage));
        OnPropertyChanged(nameof(HasRoundingValidationWarning));
    }

    private void NotifyRoundingDisplayChanged()
    {
        OnPropertyChanged(nameof(ShowRetailRoundingTrace));
        OnPropertyChanged(nameof(RetailMathBaseValue));
        OnPropertyChanged(nameof(RetailRoundingDeltaValue));
        OnPropertyChanged(nameof(RetailEffectiveMarginPct));
        OnPropertyChanged(nameof(RetailMathBaseText));
        OnPropertyChanged(nameof(RetailRoundingDeltaText));
        OnPropertyChanged(nameof(RetailEffectiveMarginText));
        OnPropertyChanged(nameof(ShowRetailCashFriendly));
        OnPropertyChanged(nameof(RetailCashFriendlyBsS));
        OnPropertyChanged(nameof(RetailCashFriendlyText));
        OnPropertyChanged(nameof(ShowWholesaleRoundingTrace));
        OnPropertyChanged(nameof(WholesaleMathBaseValue));
        OnPropertyChanged(nameof(WholesaleRoundingDeltaValue));
        OnPropertyChanged(nameof(WholesaleEffectiveMarginPct));
        OnPropertyChanged(nameof(WholesaleMathBaseText));
        OnPropertyChanged(nameof(WholesaleRoundingDeltaText));
        OnPropertyChanged(nameof(WholesaleEffectiveMarginText));
        OnPropertyChanged(nameof(ShowWholesaleCashFriendly));
        OnPropertyChanged(nameof(WholesaleCashFriendlyBsS));
        OnPropertyChanged(nameof(WholesaleCashFriendlyText));
    }

    // --- Formateo (convenciones del diálogo: $ USD, Bs.S con el formato de moneda vigente) ---

    private static string FormatUsd(decimal? value)
        => value.HasValue ? $"${CurrencyDisplay.Number(value.Value)}" : "—";

    private static string FormatSignedUsd(decimal? value)
    {
        if (!value.HasValue)
        {
            return "—";
        }

        var sign = value.Value > 0m ? "+" : value.Value < 0m ? "-" : string.Empty;
        return $"{sign}${CurrencyDisplay.Number(Math.Abs(value.Value))}";
    }

    private static string FormatMarginPct(decimal? value)
        => value.HasValue ? $"{value.Value.ToString("0.##", CultureInfo.InvariantCulture)}%" : "—";

    private static string FormatBsS(decimal value) => $"Bs.S {CurrencyDisplay.Number(value)}";
}
