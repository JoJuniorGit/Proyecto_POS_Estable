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
/// 8.183-T2 (R4): el preview del diálogo distingue el USD persistido del Bs.S redondeado en caliente
/// con la tasa del día. La proyección de efectivo (CashFriendlyAfterBsS) se mapea y se formatea con el
/// patrón Bs.S del diálogo —incluidas las filas VES informativas donde el USD no cambia— y sin
/// proyección las columnas muestran "—" (nunca se presenta como valor persistido).
/// </summary>
public class BulkPriceEditCashFriendlyTests
{
    private readonly Mock<IProductService> _productServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private BulkPriceEditDialogViewModel CreateViewModel()
        => new(
            _productServiceMock.Object,
            _dialogServiceMock.Object,
            new BulkPriceSelection(new List<int> { 1 }, null, false, 1));

    private static void EnableRetailPriceOperation(BulkPriceEditDialogViewModel viewModel, string valueText = "2")
    {
        var row = viewModel.Operations.Single(r => r.Field == BulkPriceField.RetailPrice);
        row.IsEnabled = true;
        row.ValueText = valueText;
    }

    private void SetupPreview(BulkPriceUpdateResultDto result)
        => _productServiceMock
            .Setup(s => s.PreviewBulkPricesAsync(
                It.IsAny<BulkPriceUpdateRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task Preview_OnVesInformationalRow_MapsFormattedCashFriendlyProjectionPerChannel()
    {
        var result = new BulkPriceUpdateResultDto
        {
            ExchangeRate = 842.21m,
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
                        // Fila VES informativa (R1): el USD persistido y el Bs.S canónico no cambian;
                        // la proyección de efectivo del detal (paso 5) viaja solo en CashFriendlyAfterBsS.
                        new BulkPriceFieldChangeDto
                        {
                            Field = BulkPriceField.RetailPrice,
                            BeforeValue = 0.81m,
                            AfterValue = 0.81m,
                            BeforeBsS = 682.20m,
                            AfterBsS = 682.20m,
                            PreRoundingValue = 682.20m,
                            RoundingDelta = -2.20m,
                            CashFriendlyAfterBsS = 680m
                        },
                        // Mayor con regla VES: el Bs.S informativo del canal ES la proyección de efectivo.
                        new BulkPriceFieldChangeDto
                        {
                            Field = BulkPriceField.WholesalePrice,
                            BeforeValue = 0.95m,
                            AfterValue = 0.98m,
                            BeforeBsS = 800.10m,
                            AfterBsS = 825m,
                            PreRoundingValue = 825.37m,
                            RoundingDelta = -0.37m,
                            CashFriendlyAfterBsS = 825m
                        }
                    }
                }
            }
        };
        SetupPreview(result);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.VesCashFriendlyStep5;
        vm.WholesaleRoundingRule = RoundingRuleType.VesCashFriendlyStep5;

        await vm.PreviewCommand.ExecuteAsync(null);

        var row = vm.PreviewRows.Single(r => r.ProductId == 1);
        var retailChange = row.Changes.Single(c => c.Field == BulkPriceField.RetailPrice);
        var wholesaleChange = row.Changes.Single(c => c.Field == BulkPriceField.WholesalePrice);

        // USD persistido intacto + proyección formateada con el patrón Bs.S vigente del diálogo.
        Assert.Equal(0.81m, retailChange.AfterValue);
        Assert.Equal(680m, retailChange.CashFriendlyAfterBsS);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(680m)}", retailChange.CashFriendlyText);
        Assert.Equal($"Bs.S {CurrencyDisplay.Number(825m)}", wholesaleChange.CashFriendlyText);

        // Columna del preview: una línea por canal con proyección.
        Assert.Equal(
            $"Detal: Bs.S {CurrencyDisplay.Number(680m)}{Environment.NewLine}Mayor: Bs.S {CurrencyDisplay.Number(825m)}",
            row.CashFriendlyText);
    }

    [Fact]
    public async Task Preview_WithoutCashFriendlyProjection_ShowsDashPlaceholder()
    {
        var result = new BulkPriceUpdateResultDto
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
                        // Regla psicológica USD: no hay proyección de efectivo Bs.S.
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
        SetupPreview(result);

        var vm = CreateViewModel();
        EnableRetailPriceOperation(vm);
        vm.RetailRoundingRule = RoundingRuleType.UsdPsychological;

        await vm.PreviewCommand.ExecuteAsync(null);

        var updatedRow = vm.PreviewRows.Single(r => r.ProductId == 1);
        var change = updatedRow.Changes.Single(c => c.Field == BulkPriceField.RetailPrice);
        Assert.Null(change.CashFriendlyAfterBsS);
        Assert.Null(change.CashFriendlyText);
        Assert.Equal("—", updatedRow.CashFriendlyText);
        Assert.Equal("—", vm.PreviewRows.Single(r => r.ProductId == 2).CashFriendlyText);
    }
}
