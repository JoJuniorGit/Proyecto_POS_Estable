using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.182-T2 (S6): reglas de redondeo por canal y protección del piso de margen en el diálogo de
/// edición masiva — contrato del request (reglas/pisos/enforce), validaciones client-side de los
/// pisos, mapeo de las columnas nuevas del preview (Matemático, Δ Redondeo, márgenes efectivos) e
/// invalidación de la vista previa al editar la configuración. Los tests 8.181 del diálogo
/// permanecen intactos.
/// </summary>
public class BulkPriceEditRoundingTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private BulkPriceEditDialogViewModel CreateViewModel()
        => new(
            _productServiceMock.Object,
            _dialogServiceMock.Object,
            new BulkPriceSelection(new List<int> { 1, 2 }, null, false, 2));

    private static void EnableRetailPriceOperation(BulkPriceEditDialogViewModel viewModel, string valueText = "2")
    {
        var row = viewModel.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        row.IsEnabled = true;
        row.ValueText = valueText;
    }

    private void SetupPreview(BulkPriceUpdateResultDto? result = null, Action<BulkPriceUpdateRequestDto>? capture = null)
    {
        var setup = _productServiceMock.Setup(s => s.PreviewBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()));

        if (capture is not null)
        {
            setup.Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => capture(request));
        }

        setup.ReturnsAsync(result ?? PreviewResult());
    }

    private void SetupApply(BulkPriceUpdateResultDto? result = null, Action<BulkPriceUpdateRequestDto>? capture = null)
    {
        var setup = _productServiceMock.Setup(s => s.ApplyBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()));

        if (capture is not null)
        {
            setup.Callback((BulkPriceUpdateRequestDto request, CancellationToken _) => capture(request));
        }

        setup.ReturnsAsync(result ?? PreviewResult());
    }

    private static BulkPriceUpdateResultDto PreviewResult() => new()
    {
        ExchangeRate = 50m,
        SelectedCount = 2,
        UpdatedCount = 1,
        SkippedCount = 1,
        Rows =
        {
            new BulkPricePreviewRowDto
            {
                ProductId = 1,
                SKU = "A-1",
                Name = "Arroz",
                Changes =
                {
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.RetailPrice,
                        BeforeValue = 1.20m,
                        AfterValue = 1.50m,
                        BeforeBsS = 60m,
                        AfterBsS = 75m
                    }
                }
            },
            new BulkPricePreviewRowDto
            {
                ProductId = 2,
                SKU = "B-2",
                Name = "Harina",
                SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales."
            }
        }
    };

    /// <summary>
    /// Fila con traza de redondeo USD (detal) y Bs.S (mayor) + márgenes efectivos por canal.
    /// Los valores respetan el contrato del backend: el redondeado es Matemático + Δ en la unidad
    /// de la regla (USD en <c>AfterValue</c>; Bs.S en <c>AfterBsS</c> informativo del mayor).
    /// </summary>
    private static BulkPriceUpdateResultDto RoundingPreviewResult() => new()
    {
        ExchangeRate = 50m,
        SelectedCount = 2,
        UpdatedCount = 1,
        SkippedCount = 1,
        Rows =
        {
            new BulkPricePreviewRowDto
            {
                ProductId = 1,
                SKU = "A-1",
                Name = "Arroz",
                EffectiveRetailMarginPct = 25.5m,
                EffectiveWholesaleMarginPct = 12.25m,
                Changes =
                {
                    // Spec V0.15: con la capa cash activa la traza viaja en Bs.S (detal: 73 → 75).
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.RetailPrice,
                        BeforeValue = 1.47m,
                        AfterValue = 1.50m,
                        BeforeBsS = 73.50m,
                        AfterBsS = 75m,
                        PreRoundingValue = 73.00m,
                        RoundingDelta = 2.00m,
                        CashFriendlyAfterBsS = 75m
                    },
                    // Mayor con la capa cash paso 5: Bs.S matemático 61 → escalón 60 hacia abajo (−1).
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.WholesalePrice,
                        BeforeValue = 1.20m,
                        AfterValue = 1.22m,
                        BeforeBsS = 60m,
                        AfterBsS = 60m,
                        PreRoundingValue = 61m,
                        RoundingDelta = -1m,
                        CashFriendlyAfterBsS = 60m
                    }
                }
            },
            new BulkPricePreviewRowDto
            {
                ProductId = 2,
                SKU = "B-2",
                Name = "Harina",
                SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales."
            }
        }
    };

    [Fact]
    public void RoundingOptions_SplitUsdAndCashLists_AndDefaultsAreExactMathUnenforced()
    {
        var vm = CreateViewModel();

        // Spec V0.15: el redondeo USD por canal es USD-only (exacto / psicológico).
        Assert.Equal(
            new[] { "Sin redondeo (exacto)", "Psicológico USD (.99/.95/.75/.50/.25)" },
            vm.UsdRoundingRuleOptions.Select(option => option.Label).ToArray());
        Assert.Equal(
            new[] { RoundingRuleType.ExactMath, RoundingRuleType.UsdPsychological },
            vm.UsdRoundingRuleOptions.Select(option => option.Rule).ToArray());

        // Y la proyección Bs.S es una capa aparte con sus propias sub-opciones.
        Assert.Equal(
            new[] { "Sin redondeo", "Efectivo rápido Bs.S (entero)", "(múltiplo de 5)", "(múltiplo de 10)" },
            vm.CashBsRoundingRuleOptions.Select(option => option.Label).ToArray());
        Assert.Equal(
            new[]
            {
                RoundingRuleType.ExactMath,
                RoundingRuleType.VesCashFriendlyInteger,
                RoundingRuleType.VesCashFriendlyStep5,
                RoundingRuleType.VesCashFriendlyStep10
            },
            vm.CashBsRoundingRuleOptions.Select(option => option.Rule).ToArray());

        Assert.Equal(RoundingRuleType.ExactMath, vm.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.WholesaleRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.CashBsRoundingRule);
        Assert.False(vm.EnforceMinimumMarginFloor);
        Assert.Equal(string.Empty, vm.RetailMarginFloorText);
        Assert.Equal(string.Empty, vm.WholesaleMarginFloorText);
    }

    [Fact]
    public async Task Preview_ByDefault_SendsExactMathRulesWithoutFloorsNorEnforcement()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(RoundingRuleType.ExactMath, captured!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, captured.WholesaleRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, captured.CashBsRoundingRule);
        // Spec V0.15: los baselines globales viajan para comparar "null hereda global" en el motor.
        Assert.Equal(RoundingRuleType.ExactMath, captured.GlobalUsdRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, captured.GlobalCashBsRoundingRule);
        Assert.False(captured.EnforceMinimumMarginFloor);
        Assert.Null(captured.RetailMarginFloorPct);
        Assert.Null(captured.WholesaleMarginFloorPct);
    }

    [Fact]
    public async Task Preview_WithRulesAndFloorProtection_SendsRulesAndParsedFloors()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10;
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = "25";
        vm.WholesaleMarginFloorText = "12,5";

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(RoundingRuleType.UsdPsychological, captured!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.UsdPsychological, captured.WholesaleRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, captured.CashBsRoundingRule);
        Assert.True(captured.EnforceMinimumMarginFloor);
        Assert.Equal(25m, captured.RetailMarginFloorPct);
        Assert.Equal(12.5m, captured.WholesaleMarginFloorPct);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task Preview_WhenFloorProtectionDisabled_IgnoresFloorTextsAndSendsNull()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailMarginFloorText = "abc";
        vm.WholesaleMarginFloorText = "-5";

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.False(captured!.EnforceMinimumMarginFloor);
        Assert.Null(captured.RetailMarginFloorPct);
        Assert.Null(captured.WholesaleMarginFloorPct);
        Assert.Empty(vm.ErrorMessage);
        Assert.True(vm.HasPreview);
    }

    [Theory]
    [InlineData("abc", "")]
    [InlineData("-5", "")]
    [InlineData("1.5.0", "")]
    [InlineData("", "-1")]
    public async Task Preview_WithInvalidMarginFloorText_ShowsValidationErrorAndDoesNotCallService(
        string retailFloorText,
        string wholesaleFloorText)
    {
        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = retailFloorText;
        vm.WholesaleMarginFloorText = wholesaleFloorText;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(BulkPriceEditDialogViewModel.InvalidMarginFloorMessage, vm.ErrorMessage);
        Assert.False(vm.HasPreview);
        _productServiceMock.Verify(s => s.PreviewBulkPricesAsync(
            It.IsAny<BulkPriceUpdateRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Preview_WithEmptyFloorTexts_SendsNullFloorsWhenProtectionEnabled()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = " ";
        vm.WholesaleMarginFloorText = string.Empty;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.True(captured!.EnforceMinimumMarginFloor);
        Assert.Null(captured.RetailMarginFloorPct);
        Assert.Null(captured.WholesaleMarginFloorPct);
        Assert.True(vm.HasPreview);
    }

    [Fact]
    public async Task Preview_MapsRoundingTraceInBsSUnit_WhenCashProjectionIsActive()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(RoundingPreviewResult(), capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        await vm.PreviewCommand.ExecuteAsync(null);

        // Spec V0.15: la capa cash viaja en el contrato y define la unidad de la traza (Bs.S).
        Assert.NotNull(captured);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep5, captured!.CashBsRoundingRule);

        var updatedRow = vm.PreviewRows.Single(r => r.ProductId == 1);
        var retailChange = updatedRow.Changes.Single(c => c.Field == BulkPriceField.RetailPrice);
        var wholesaleChange = updatedRow.Changes.Single(c => c.Field == BulkPriceField.WholesalePrice);

        // Capa cash activa: matemático/redondeado/Δ en Bs.S para AMBOS canales.
        Assert.Equal(73.00m, retailChange.PreRoundingValue);
        Assert.Equal(2.00m, retailChange.RoundingDelta);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(73.00m)}", retailChange.PreRoundingText);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(75.00m)}", retailChange.RoundedText);
        Assert.Equal($"+Bs.S {CurrencyDisplay.Number(2.00m)}", retailChange.RoundingDeltaText);

        Assert.Equal(61m, wholesaleChange.PreRoundingValue);
        Assert.Equal(-1m, wholesaleChange.RoundingDelta);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(61m)}", wholesaleChange.PreRoundingText);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(60m)}", wholesaleChange.RoundedText);
        Assert.Equal($"-Bs.S {CurrencyDisplay.Number(1m)}", wholesaleChange.RoundingDeltaText);

        // Columnas agregadas por fila: una línea por canal con traza.
        Assert.Equal(
            $"Detal: Bs.S {CurrencyDisplay.Number(73.00m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(61m)}",
            updatedRow.MathText);
        Assert.Equal(
            $"Detal: Bs.S {CurrencyDisplay.Number(75.00m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(60m)}",
            updatedRow.RoundedText);
        Assert.Equal(
            $"Detal: +Bs.S {CurrencyDisplay.Number(2.00m)}{Environment.NewLine}Mayor: -Bs.S {CurrencyDisplay.Number(1m)}",
            updatedRow.RoundingDeltaText);

        // Márgenes efectivos (solo cuando el backend los informa).
        Assert.Equal(25.5m, updatedRow.EffectiveRetailMarginPct);
        Assert.Equal(12.25m, updatedRow.EffectiveWholesaleMarginPct);
        Assert.Equal("25.5%", updatedRow.EffectiveRetailMarginText);
        Assert.Equal("12.25%", updatedRow.EffectiveWholesaleMarginText);

        // Fila sin cambios: sin traza ni márgenes → marcador vacío en las columnas nuevas.
        var skippedRow = vm.PreviewRows.Single(r => r.ProductId == 2);
        Assert.Equal("—", skippedRow.MathText);
        Assert.Equal("—", skippedRow.RoundedText);
        Assert.Equal("—", skippedRow.RoundingDeltaText);
        Assert.Equal("—", skippedRow.EffectiveRetailMarginText);
        Assert.Equal("—", skippedRow.EffectiveWholesaleMarginText);
    }

    [Fact]
    public async Task Preview_MapsRoundingTraceInUsdUnit_WhenCashProjectionIsExactMath()
    {
        var result = new BulkPriceUpdateResultDto
        {
            ExchangeRate = 50m,
            SelectedCount = 1,
            UpdatedCount = 1,
            SkippedCount = 0,
            Rows =
            {
                new BulkPricePreviewRowDto
                {
                    ProductId = 1,
                    SKU = "A-1",
                    Name = "Arroz",
                    Changes =
                    {
                        // Traza del redondeo psicológico USD (la capa cash está apagada).
                        new BulkPriceFieldChangeDto
                        {
                            Field = BulkPriceField.RetailPrice,
                            BeforeValue = 1.47m,
                            AfterValue = 1.50m,
                            BeforeBsS = 73.50m,
                            AfterBsS = 75m,
                            PreRoundingValue = 1.46m,
                            RoundingDelta = 0.04m
                        }
                    }
                }
            }
        };
        SetupPreview(result);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        await vm.PreviewCommand.ExecuteAsync(null);

        var change = vm.PreviewRows.Single().Changes.Single();
        Assert.Equal($"${CurrencyDisplay.Number(1.46m)}", change.PreRoundingText);
        Assert.Equal($"${CurrencyDisplay.Number(1.50m)}", change.RoundedText);
        Assert.Equal($"+${CurrencyDisplay.Number(0.04m)}", change.RoundingDeltaText);
    }

    [Fact]
    public async Task EditingRoundingRuleAfterPreview_InvalidatesPreview()
    {
        SetupPreview();

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);
        Assert.True(vm.CanApply);

        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        Assert.False(vm.HasPreview);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public async Task EditingCashBsRoundingRuleAfterPreview_InvalidatesPreview()
    {
        SetupPreview();

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);
        Assert.True(vm.CanApply);

        // Spec V0.15: la capa cash es configuración del lote — cambiarla descarta la vista previa.
        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyInteger;

        Assert.False(vm.HasPreview);
        Assert.False(vm.CanApply);
        Assert.Empty(vm.PreviewRows);
    }

    [Fact]
    public async Task EditingFloorConfigurationAfterPreview_InvalidatesPreview()
    {
        SetupPreview();

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);

        vm.EnforceMinimumMarginFloor = true;
        Assert.False(vm.HasPreview);

        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreview);

        vm.RetailMarginFloorText = "30";
        Assert.False(vm.HasPreview);
    }

    [Fact]
    public async Task Apply_AfterRoundingPreview_ReusesRequestWithRulesAndFloors()
    {
        BulkPriceUpdateRequestDto? previewRequest = null;
        BulkPriceUpdateRequestDto? applyRequest = null;
        SetupPreview(capture: request => previewRequest = request);
        SetupApply(capture: request => applyRequest = request);
        _dialogServiceMock
            .Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10;
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = "20";

        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Same(previewRequest, applyRequest);
        Assert.NotNull(applyRequest);
        Assert.Equal(RoundingRuleType.UsdPsychological, applyRequest!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, applyRequest.CashBsRoundingRule);
        Assert.True(applyRequest.EnforceMinimumMarginFloor);
        Assert.Equal(20m, applyRequest.RetailMarginFloorPct);
        Assert.True(vm.HasApplied);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec V0.15: la columna Antes → Después de márgenes refleja la transición en pp con formato
    // 2dp (CurrencyDisplay) y las filas de margen declaran la semántica de puntos porcentuales;
    // las filas monetarias conservan el hint porcentual.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_MapsMarginChangesWithTwoDecimalsAndSummaryTransition()
    {
        var result = new BulkPriceUpdateResultDto
        {
            ExchangeRate = 50m,
            SelectedCount = 1,
            UpdatedCount = 1,
            SkippedCount = 0,
            Rows =
            {
                new BulkPricePreviewRowDto
                {
                    ProductId = 1,
                    SKU = "A-1",
                    Name = "Arroz",
                    Changes =
                    {
                        new BulkPriceFieldChangeDto
                        {
                            Field = BulkPriceField.RetailMargin,
                            BeforeValue = 10m,
                            AfterValue = 20m
                        }
                    }
                }
            }
        };
        SetupPreview(result);

        var vm = CreateViewModel();
        var row = vm.Operations.Single(r => r.Field == BulkPriceField.RetailMargin);
        row.IsEnabled = true;
        row.SelectedMode = BulkPriceMode.IncreasePercent;
        row.ValueText = "10";

        await vm.PreviewCommand.ExecuteAsync(null);

        var change = Assert.Single(vm.PreviewRows.Single().Changes);
        Assert.Equal($"{CurrencyDisplay.Number(10m)}%", change.BeforeText);
        Assert.Equal($"{CurrencyDisplay.Number(20m)}%", change.AfterText);
        Assert.Equal($"Margen Detal: {CurrencyDisplay.Number(10m)}% → {CurrencyDisplay.Number(20m)}%", change.Summary);
    }

    [Fact]
    public void OperationRows_HintPercentagePointsForMargins_AndPercentageForMonetaryFields()
    {
        var vm = CreateViewModel();

        var marginRow = vm.Operations.Single(r => r.Field == BulkPriceField.RetailMargin);
        marginRow.IsEnabled = true;
        marginRow.SelectedMode = BulkPriceMode.IncreasePercent;
        Assert.Equal("Puntos porcentuales sobre el margen actual", marginRow.ValueHint);

        marginRow.SelectedMode = BulkPriceMode.DecreasePercent;
        Assert.Equal("Puntos porcentuales sobre el margen actual", marginRow.ValueHint);

        marginRow.SelectedMode = BulkPriceMode.SetFixed;
        Assert.Equal("Valor absoluto (%)", marginRow.ValueHint);

        var priceRow = vm.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        priceRow.IsEnabled = true;
        priceRow.SelectedMode = BulkPriceMode.IncreasePercent;
        Assert.Equal("Porcentaje sobre el valor actual", priceRow.ValueHint);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec V0.15: los pisos de margen se muestran SOLO con redondeo psicológico USD + protección
    // activa (por canal). Ocultar el input no borra el valor tipeado: sigue viajando y aplicando al
    // clamp de reducciones de margen (la visibilidad es cosmética).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void FloorVisibility_HiddenWithExactMathEvenWhenProtectionIsOn()
    {
        var vm = CreateViewModel();
        vm.EnforceMinimumMarginFloor = true;

        Assert.False(vm.ShowRetailFloorInput);
        Assert.False(vm.ShowWholesaleFloorInput);
    }

    [Fact]
    public void FloorVisibility_HiddenWhenProtectionIsOffEvenWithPsychologicalRules()
    {
        var vm = CreateViewModel();
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;

        Assert.False(vm.EnforceMinimumMarginFloor);
        Assert.False(vm.ShowRetailFloorInput);
        Assert.False(vm.ShowWholesaleFloorInput);
    }

    [Fact]
    public void FloorVisibility_ShownOnlyWithPsychologicalUsdAndProtection_PerChannel()
    {
        var vm = CreateViewModel();
        vm.EnforceMinimumMarginFloor = true;

        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(vm.ShowRetailFloorInput);
        Assert.False(vm.ShowWholesaleFloorInput); // el canal al mayor sigue en exacto

        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(vm.ShowWholesaleFloorInput);

        // Sin psicológico el piso jamás se muestra, aunque el toggle esté activo.
        vm.RetailRoundingRule = RoundingRuleType.ExactMath;
        Assert.False(vm.ShowRetailFloorInput);
        Assert.True(vm.ShowWholesaleFloorInput);
    }

    [Fact]
    public void FloorVisibility_RaisesPropertyChangedOnToggleAndRuleChanges()
    {
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.ShowRetailFloorInput), changed);
        changed.Clear();

        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.ShowWholesaleFloorInput), changed);
        changed.Clear();

        vm.EnforceMinimumMarginFloor = true;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.ShowRetailFloorInput), changed);
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.ShowWholesaleFloorInput), changed);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec V0.15: habilitación OR (operaciones válidas O modificación de redondeo/protección),
    // columnas consolidadas de la tabla (5 columnas) y estado vacío por filas reales.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CanPreview_Disabled_WithoutOperationsOrRoundingConfiguration()
    {
        var vm = CreateViewModel();

        Assert.False(vm.CanPreview);
        Assert.False(vm.CanApply);

        // Toggle activo sin piso numérico definido tampoco habilita.
        vm.EnforceMinimumMarginFloor = true;
        Assert.False(vm.CanPreview);

        // Un piso "0" no cuenta como definido.
        vm.RetailMarginFloorText = "0";
        Assert.False(vm.CanPreview);
    }

    [Fact]
    public void CanPreview_EnabledWithValidOperation_AndDisabledWhileValueIsInvalid()
    {
        var vm = CreateViewModel();
        var row = vm.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);

        row.IsEnabled = true;
        Assert.False(vm.CanPreview); // marcada sin valor válido

        row.ValueText = "abc";
        Assert.False(vm.CanPreview);

        row.ValueText = "10";
        Assert.True(vm.CanPreview);
    }

    [Fact]
    public void CanPreview_EnabledWithRoundingRuleOrDefinedFloor_WithoutOperations()
    {
        var retailRule = CreateViewModel();
        retailRule.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(retailRule.CanPreview);

        var wholesaleRule = CreateViewModel();
        wholesaleRule.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(wholesaleRule.CanPreview);

        var cashRule = CreateViewModel();
        cashRule.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyInteger;
        Assert.True(cashRule.CanPreview);

        var retailFloor = CreateViewModel();
        retailFloor.EnforceMinimumMarginFloor = true;
        retailFloor.RetailMarginFloorText = "25";
        Assert.True(retailFloor.CanPreview);

        var wholesaleFloor = CreateViewModel();
        wholesaleFloor.EnforceMinimumMarginFloor = true;
        wholesaleFloor.WholesaleMarginFloorText = "12,5";
        Assert.True(wholesaleFloor.CanPreview);
    }

    [Fact]
    public async Task Preview_WithZeroOperationsAndRoundingRule_SendsEmptyOperationsAndRule()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Empty(captured!.Operations);
        Assert.Equal(RoundingRuleType.UsdPsychological, captured.RetailRoundingRule);
        Assert.True(vm.HasPreview);
        Assert.True(vm.CanApply); // PreviewResult() trae UpdatedCount 1
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public void CanPreview_RaisesPropertyChanged_OnOperationsRulesToggleAndFloors()
    {
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.CanPreview), changed);
        changed.Clear();

        vm.EnforceMinimumMarginFloor = true;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.CanPreview), changed);
        changed.Clear();

        vm.RetailMarginFloorText = "25";
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.CanPreview), changed);
        changed.Clear();

        var row = vm.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        row.IsEnabled = true;
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.CanPreview), changed);
        changed.Clear();

        row.ValueText = "10";
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.CanPreview), changed);
    }

    [Fact]
    public async Task Preview_ConsolidatedColumns_BuildUsdCashAndMarginLines()
    {
        SetupPreview(RoundingPreviewResult());

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        await vm.PreviewCommand.ExecuteAsync(null);

        var row = vm.PreviewRows.Single(r => r.ProductId == 1);

        // PRECIO USD: transición por canal en USD, sin bolívares.
        Assert.Equal(
            $"Detal: ${CurrencyDisplay.Number(1.47m)} → ${CurrencyDisplay.Number(1.50m)}{Environment.NewLine}" +
            $"Mayor: ${CurrencyDisplay.Number(1.20m)} → ${CurrencyDisplay.Number(1.22m)}",
            row.UsdTransitionText);

        // BS.S EFECTIVO (PROY.): redondeado + delta de redondeo entre paréntesis cuando existe.
        Assert.Equal(
            $"Detal: Bs.S {CurrencyDisplay.Number(75m)} (+Bs.S {CurrencyDisplay.Number(2m)}){Environment.NewLine}" +
            $"Mayor: Bs.S {CurrencyDisplay.Number(60m)} (-Bs.S {CurrencyDisplay.Number(1m)})",
            row.CashFriendlyText);

        // El Bs.S matemático exacto viaja en el ToolTip.
        Assert.Equal(
            $"Detal: Bs.S {CurrencyDisplay.Number(73m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(61m)}",
            row.CashFriendlyToolTip);

        // MARGEN EFECTIVO %: ambos canales en la misma celda.
        Assert.Equal($"Detal: 25.5%{Environment.NewLine}Mayor: 12.25%", row.EffectiveMarginsText);
    }

    [Fact]
    public void CashProjectionColumn_OmitsParenthesisWhenRoundingDeltaIsZero()
    {
        var row = BulkPricePreviewRowViewModel.FromDto(
            new BulkPricePreviewRowDto
            {
                ProductId = 1,
                SKU = "A-1",
                Name = "Arroz",
                Changes =
                {
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.RetailPrice,
                        BeforeValue = 1.20m,
                        AfterValue = 1.20m,
                        BeforeBsS = 120m,
                        AfterBsS = 120m,
                        PreRoundingValue = 120m,
                        RoundingDelta = 0m,
                        CashFriendlyAfterBsS = 120m
                    }
                }
            },
            RoundingRuleType.VesCashFriendlyInteger);

        Assert.Equal($"Detal: Bs.S {CurrencyDisplay.Number(120m)}", row.CashFriendlyText);
        Assert.Equal($"Detal: Bs.S {CurrencyDisplay.Number(120m)}", row.CashFriendlyToolTip);
    }

    [Fact]
    public async Task HasPreviewItems_TracksRowsPresence_ForEmptyStateOverlay()
    {
        SetupPreview();

        var vm = CreateViewModel();
        Assert.False(vm.HasPreviewItems);

        EnableRetailPriceOperation(vm);
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.True(vm.HasPreviewItems);

        var row = vm.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        row.SelectedMode = BulkPriceMode.IncreasePercent; // invalida la vista previa y vacía las filas

        Assert.False(vm.HasPreviewItems);
        Assert.Empty(vm.PreviewRows);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec V0.15 (pulido): overlay por filas reales (independiente de UpdatedCount), mensaje ámbar
    // cuando no hay cambios aplicables y píldora de ESTADO con motivo corto + tooltip de política.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_WithOnlyOmittedRows_CollapsesOverlayAndFlagsAmberMessage()
    {
        var result = new BulkPriceUpdateResultDto
        {
            ExchangeRate = 50m,
            SelectedCount = 1,
            UpdatedCount = 0,
            SkippedCount = 1,
            Rows =
            {
                new BulkPricePreviewRowDto
                {
                    ProductId = 1,
                    SKU = "A-1",
                    Name = "Arroz",
                    SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales."
                }
            }
        };
        SetupPreview(result);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);

        await vm.PreviewCommand.ExecuteAsync(null);

        // Hay filas (Omitido) → el overlay queda colapsado aunque UpdatedCount sea 0.
        Assert.True(vm.HasPreviewItems);
        Assert.False(vm.CanApply);
        Assert.True(vm.StatusMessageIsWarning);
        Assert.Equal("La vista previa no contiene cambios a aplicar.", vm.StatusMessage);
    }

    [Fact]
    public async Task Preview_WithApplicableChanges_ClearsAmberWarning()
    {
        SetupPreview();

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.True(vm.HasPreviewItems);
        Assert.False(vm.StatusMessageIsWarning);
        Assert.Equal("Vista previa lista: revise los cambios antes de aplicar.", vm.StatusMessage);
    }

    [Fact]
    public void StatusBadge_ShortensOmittedReason_AndKeepsPillColors()
    {
        var omitted = BulkPricePreviewRowViewModel.FromDto(
            new BulkPricePreviewRowDto
            {
                ProductId = 1,
                SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales."
            },
            RoundingRuleType.ExactMath);
        Assert.Equal("Sin cambios", omitted.StatusBadgeText);
        Assert.Equal("#E2E8F0", omitted.StatusBadgeBackground);
        Assert.Equal("#475569", omitted.StatusBadgeForeground);
        Assert.Contains("Sin cambios:", omitted.StatusText); // el tooltip conserva el motivo completo

        var updating = BulkPricePreviewRowViewModel.FromDto(new BulkPricePreviewRowDto { ProductId = 2 }, RoundingRuleType.ExactMath);
        Assert.Equal("Actualiza", updating.StatusBadgeText);
        Assert.Equal("#DCFCE7", updating.StatusBadgeBackground);
        Assert.Equal("#166534", updating.StatusBadgeForeground);

        var error = BulkPricePreviewRowViewModel.FromDto(
            new BulkPricePreviewRowDto { ProductId = 3, IsError = true, SkipReason = "El valor resultante no puede ser negativo (precio al detal)." },
            RoundingRuleType.ExactMath);
        Assert.Equal("Error", error.StatusBadgeText);
        Assert.Equal("#FEE2E2", error.StatusBadgeBackground);
        Assert.Equal("#B91C1C", error.StatusBadgeForeground);
    }

    [Fact]
    public void StatusToolTip_AppendsPolicyNote_WhenPresent()
    {
        var row = BulkPricePreviewRowViewModel.FromDto(
            new BulkPricePreviewRowDto
            {
                ProductId = 1,
                PolicyNote = "Reglas USD distintas por canal (Detal/Mayor): la política por artículo no se materializa."
            },
            RoundingRuleType.ExactMath);

        Assert.Contains("Actualiza", row.StatusToolTip);
        Assert.Contains("por canal", row.StatusToolTip);
    }

    [Fact]
    public void EffectiveMarginsText_ShowsRealMargin_ForSingleChannel()
    {
        var row = BulkPricePreviewRowViewModel.FromDto(
            new BulkPricePreviewRowDto { ProductId = 1, EffectiveRetailMarginPct = 62.3m },
            RoundingRuleType.ExactMath);

        Assert.Equal("Detal: 62.3%", row.EffectiveMarginsText);
    }

    // ---------------------------------------------------------------------------------------------
    // Spec V0.15 (intención explícita): los flags Touch solo se activan con un cambio del selector
    // respecto de su seed, o en Solo Redondeo con la regla activa; un lote de sección 1 no toca nada.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TouchFlags_PlainSectionOneBatch_AreFalseAndTravelInRequest()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);

        Assert.False(vm.TouchRetailUsdPolicy);
        Assert.False(vm.TouchWholesaleUsdPolicy);
        Assert.False(vm.TouchCashBsPolicy);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.False(captured!.TouchRetailUsdPolicy);
        Assert.False(captured.TouchWholesaleUsdPolicy);
        Assert.False(captured.TouchCashBsPolicy);
    }

    [Fact]
    public void TouchFlags_TrackSelectorChanges_AndResetWhenBackToSeed()
    {
        var vm = CreateViewModel();
        var changed = new List<string>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(vm.TouchRetailUsdPolicy);
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.TouchRetailUsdPolicy), changed);
        changed.Clear();

        vm.RetailRoundingRule = RoundingRuleType.ExactMath; // volver al seed = sin touch
        Assert.False(vm.TouchRetailUsdPolicy);
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.TouchRetailUsdPolicy), changed);
        changed.Clear();

        vm.WholesaleRoundingRule = RoundingRuleType.UsdPsychological;
        Assert.True(vm.TouchWholesaleUsdPolicy);
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.TouchWholesaleUsdPolicy), changed);
        changed.Clear();

        vm.CashBsRoundingRule = RoundingRuleType.VesCashFriendlyStep10;
        Assert.True(vm.TouchCashBsPolicy);
        Assert.Contains(nameof(BulkPriceEditDialogViewModel.TouchCashBsPolicy), changed);
        changed.Clear();

        vm.CashBsRoundingRule = RoundingRuleType.ExactMath;
        Assert.False(vm.TouchCashBsPolicy);
    }

    [Fact]
    public async Task TouchFlags_SoloRedondeo_TouchOnlyActiveRules()
    {
        BulkPriceUpdateRequestDto? captured = null;
        SetupPreview(capture: request => captured = request);

        var vm = CreateViewModel();
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological; // sin operaciones: Solo Redondeo

        Assert.True(vm.TouchRetailUsdPolicy);
        Assert.False(vm.TouchWholesaleUsdPolicy); // sigue en exacto
        Assert.False(vm.TouchCashBsPolicy);

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.True(captured!.TouchRetailUsdPolicy);
        Assert.False(captured.TouchWholesaleUsdPolicy);
        Assert.False(captured.TouchCashBsPolicy);
    }
}
