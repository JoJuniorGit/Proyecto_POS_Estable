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
                    // Detal con regla psicológica USD: 1.46 matemático → .50 redondeado (+0.04).
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.RetailPrice,
                        BeforeValue = 1.47m,
                        AfterValue = 1.50m,
                        BeforeBsS = 73.50m,
                        AfterBsS = 75m,
                        PreRoundingValue = 1.46m,
                        RoundingDelta = 0.04m
                    },
                    // Mayor con regla VES paso 5: Bs.S matemático 61 → escalón 60 hacia abajo (−1).
                    new BulkPriceFieldChangeDto
                    {
                        Field = BulkPriceField.WholesalePrice,
                        BeforeValue = 1.20m,
                        AfterValue = 1.22m,
                        BeforeBsS = 60m,
                        AfterBsS = 60m,
                        PreRoundingValue = 61m,
                        RoundingDelta = -1m
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
    public void RoundingOptions_ExposeSpanishLabelsInOrder_AndDefaultsAreExactMathUnenforced()
    {
        var vm = CreateViewModel();

        Assert.Equal(
            new[] { "Sin redondeo (exacto)", "Psicológico USD (.99/.95/.75/.50/.25)", "Efectivo rápido Bs.S (entero)", "(múltiplo de 5)", "(múltiplo de 10)" },
            vm.RoundingRuleOptions.Select(option => option.Label).ToArray());
        Assert.Equal(
            new[]
            {
                RoundingRuleType.ExactMath,
                RoundingRuleType.UsdPsychological,
                RoundingRuleType.VesCashFriendlyInteger,
                RoundingRuleType.VesCashFriendlyStep5,
                RoundingRuleType.VesCashFriendlyStep10
            },
            vm.RoundingRuleOptions.Select(option => option.Rule).ToArray());

        Assert.Equal(RoundingRuleType.ExactMath, vm.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.ExactMath, vm.WholesaleRoundingRule);
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
        vm.WholesaleRoundingRule = RoundingRuleType.VesCashFriendlyStep10;
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = "25";
        vm.WholesaleMarginFloorText = "12,5";

        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(RoundingRuleType.UsdPsychological, captured!.RetailRoundingRule);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, captured.WholesaleRoundingRule);
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
    public async Task Preview_MapsMathDeltaAndEffectiveMargins_UsingEachChannelRuleUnit()
    {
        SetupPreview(RoundingPreviewResult());

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;
        vm.WholesaleRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        await vm.PreviewCommand.ExecuteAsync(null);

        var updatedRow = vm.PreviewRows.Single(r => r.ProductId == 1);
        var retailChange = updatedRow.Changes.Single(c => c.Field == BulkPriceField.RetailPrice);
        var wholesaleChange = updatedRow.Changes.Single(c => c.Field == BulkPriceField.WholesalePrice);

        // Unidad USD (regla psicológica): matemático, redondeado y delta con signo.
        Assert.Equal(1.46m, retailChange.PreRoundingValue);
        Assert.Equal(0.04m, retailChange.RoundingDelta);
        Assert.Equal($"${CurrencyDisplay.Number(1.46m)}", retailChange.PreRoundingText);
        Assert.Equal($"${CurrencyDisplay.Number(1.50m)}", retailChange.RoundedText);
        Assert.Equal($"+${CurrencyDisplay.Number(0.04m)}", retailChange.RoundingDeltaText);

        // Unidad Bs.S (regla efectivo rápido): redondeado = matemático + Δ (escalón 60 hacia abajo).
        Assert.Equal(61m, wholesaleChange.PreRoundingValue);
        Assert.Equal(-1m, wholesaleChange.RoundingDelta);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(61m)}", wholesaleChange.PreRoundingText);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(60m)}", wholesaleChange.RoundedText);
        Assert.Equal($"-Bs.S {CurrencyDisplay.Number(1m)}", wholesaleChange.RoundingDeltaText);

        // Columnas agregadas por fila: una línea por canal con traza.
        Assert.Equal(
            $"Detal: ${CurrencyDisplay.Number(1.46m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(61m)}",
            updatedRow.MathText);
        Assert.Equal(
            $"Detal: ${CurrencyDisplay.Number(1.50m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(60m)}",
            updatedRow.RoundedText);
        Assert.Equal(
            $"Detal: +${CurrencyDisplay.Number(0.04m)}{Environment.NewLine}Mayor: -Bs.S {CurrencyDisplay.Number(1m)}",
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
        vm.EnforceMinimumMarginFloor = true;
        vm.RetailMarginFloorText = "20";

        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Same(previewRequest, applyRequest);
        Assert.NotNull(applyRequest);
        Assert.Equal(RoundingRuleType.UsdPsychological, applyRequest!.RetailRoundingRule);
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
}
