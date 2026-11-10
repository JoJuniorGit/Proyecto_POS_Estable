using System;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Moq;
using Xunit;
using ClientSupplierInvoiceService = Desktop.Client.Services.ISupplierInvoiceService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.211-T4c2b4d3b (S9): the supplier invoice surface resolves its document currencies, rate
/// gating and labels from the active monetary profile instead of the legacy hardcoded USD/Bs.S pair.
/// </summary>
public sealed class SupplierInvoiceMonetaryProfileTests
{
    [Fact]
    public void DefaultDualProfile_ExposesUsdAndBsS_AndRequiresRateOnlyForLocalSecondary()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(null);
            var viewModel = CreateViewModel();

            Assert.Equal(new[] { CurrencyCodes.Usd, CurrencyCodes.BsS }, viewModel.CurrencyOptions);
            Assert.True(viewModel.HasSecondaryCurrency);
            Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
            Assert.False(viewModel.IsLocalSecondaryCurrency);
            Assert.False(viewModel.IsRateInputEnabled);
            Assert.True(viewModel.HasValidAppliedRate);
            Assert.False(viewModel.ShowRateValidationHint);
            Assert.Equal("Costo USD", viewModel.PrimaryCostHeader);

            viewModel.SelectedCurrency = CurrencyCodes.BsS;

            Assert.True(viewModel.IsBsSCurrency);
            Assert.True(viewModel.IsLocalSecondaryCurrency);
            Assert.True(viewModel.IsRateInputEnabled);
            Assert.Equal("Costo factura (Bs.S)", viewModel.DocumentCostHeader);
            Assert.Equal("Tasa aplicada (Bs.S)", viewModel.AppliedRateLabel);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void DualUsdCop_SelectingCopEnablesLocalRateAndLabels()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "COP",
                SecondaryCurrencySymbol = "COL$"
            });
            var viewModel = CreateViewModel();

            Assert.Equal(new[] { CurrencyCodes.Usd, "COP" }, viewModel.CurrencyOptions);
            Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
            Assert.Equal("COP", viewModel.LocalCurrencyOption);

            viewModel.SelectedCurrency = "COP";

            Assert.True(viewModel.IsLocalSecondaryCurrency);
            Assert.True(viewModel.IsBsSCurrency); // compatibility alias, independent of the ISO code
            Assert.True(viewModel.IsRateInputEnabled);
            Assert.True(viewModel.ShowRateValidationHint);
            Assert.False(viewModel.HasValidAppliedRate);
            Assert.EndsWith("(COP)", viewModel.DocumentCostHeader, StringComparison.Ordinal);
            Assert.Equal("Tasa aplicada (COP)", viewModel.AppliedRateLabel);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SingleEur_UsesOnlyPrimaryCurrency_WithoutRateInput()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            });
            var viewModel = CreateViewModel();

            Assert.Equal(new[] { "EUR" }, viewModel.CurrencyOptions);
            Assert.Equal("EUR", viewModel.SelectedCurrency);
            Assert.False(viewModel.HasSecondaryCurrency);
            Assert.False(viewModel.IsLocalSecondaryCurrency);
            Assert.False(viewModel.IsBsSCurrency);
            Assert.False(viewModel.IsRateInputEnabled);
            Assert.False(viewModel.ShowRateValidationHint);
            Assert.True(viewModel.HasValidAppliedRate);
            Assert.Equal("Costo EUR", viewModel.PrimaryCostHeader);
            Assert.EndsWith("(EUR)", viewModel.DocumentCostHeader, StringComparison.Ordinal);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SinglePrimary_PropagatesIdentityRateToLinesAndSnapshot()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            });
            var viewModel = CreateViewModel();

            viewModel.LoadStagedInvoice(CreateInvoice("EUR", CreateLine(unitCost: 12m, unitCostDocument: 12m)));

            var line = Assert.Single(viewModel.Lines);
            Assert.Equal("EUR", line.InvoiceCurrency);
            Assert.Equal(1m, line.AppliedRate);
            Assert.Equal(12m, line.UnitCostUSD);
            Assert.Equal(1m, viewModel.StagedInvoice!.AppliedRate);
            Assert.Equal("EUR", viewModel.StagedInvoice.Currency);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void DualSecondaryWithoutRate_KeepsRowsAtZeroAndBlocksInsteadOfProducingNonFiniteValues()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = "COP",
                SecondaryCurrencySymbol = "COL$"
            });
            var viewModel = CreateViewModel();
            viewModel.LoadStagedInvoice(CreateInvoice(
                CurrencyCodes.Usd,
                CreateLine(unitCost: 4000m, unitCostDocument: 4000m)));

            viewModel.SelectedCurrency = "COP";
            var line = Assert.Single(viewModel.Lines);

            Assert.Equal(0m, line.UnitCostUSD);
            Assert.Equal(0m, line.SuggestedRetailPriceUSD);
            Assert.True(viewModel.ShowRateValidationHint);
            Assert.False(viewModel.CanConfirmInvoice);

            viewModel.AppliedRateText = "4000";
            Assert.Equal(1m, line.UnitCostUSD);
            Assert.False(viewModel.ShowRateValidationHint);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SelectedSupplier_LegacyVesValue_MapsToLocalBsSOption()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(null);
            var viewModel = CreateViewModel();

            viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, "VES");

            Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    [Fact]
    public void SelectedSupplier_StoredCurrencyOutsideProfile_IsIgnored()
    {
        try
        {
            CurrencyDisplay.SetMonetaryProfile(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "EUR",
                PrimaryCurrencySymbol = "€",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null
            });
            var viewModel = CreateViewModel();

            viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, "Bs.S");

            Assert.Equal("EUR", viewModel.SelectedCurrency);
        }
        finally
        {
            CurrencyDisplay.SetMonetaryProfile(null);
        }
    }

    private static SupplierInvoiceViewModel CreateViewModel()
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(service => service.GetCurrentRateAsync())
            .ReturnsAsync((36.50m, (DateTime?)null));
        return new SupplierInvoiceViewModel(
            Mock.Of<ClientSupplierInvoiceService>(),
            userSession,
            Mock.Of<IDialogService>(),
            Mock.Of<IFilePickerDialog>(),
            rateService.Object);
    }

    private static SupplierInvoiceDetailDto CreateInvoice(string currency, params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", currency, currency == CurrencyCodes.Usd ? 1m : 36.5m, lines);

    private static SupplierInvoiceLineDto CreateLine(decimal unitCost = 10m, decimal? unitCostDocument = null) => new(
        1,
        "SUP-1",
        "12345",
        "Coffee",
        2m,
        unitCostDocument ?? unitCost,
        unitCost,
        "Update",
        9,
        8m,
        20m,
        10m,
        5m,
        20m,
        10m,
        12m,
        11m,
        true,
        "Barcode",
        null,
        null,
        null);
}
