using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Inventory.Module.Data;
using Moq;
using Xunit;
using ClientSupplierInvoiceService = Desktop.Client.Services.ISupplierInvoiceService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.176: reactividad global de la tabla de revisión de facturas de proveedor al cambiar la moneda
/// del documento o la tasa aplicada en la cabecera (título dinámico, recálculo en cascada y
/// salvaguarda de división por cero).
/// </summary>
public sealed class SupplierInvoiceCurrencyReactivityTests
{
    [Fact]
    public void DocumentCostHeader_FollowsLiveHeaderCurrencySelection()
    {
        // 8.176: el título de la columna acompaña la moneda viva de la cabecera, no el snapshot staged.
        var viewModel = CreateViewModel();
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);

        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);

        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        Assert.Equal("Costo factura (Bs.S)", viewModel.DocumentCostHeader);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DocumentCostHeader), changed);

        changed.Clear();
        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DocumentCostHeader), changed);
    }

    [Fact]
    public void LoadStagedInvoice_DoesNotReassignHeaderCurrencyOrRate_RowsFollowLiveHeader()
    {
        // 8.178: cargar/recargar la factura no re-asigna Moneda/Tasa de la cabecera (aislamiento
        // del flujo "Actualizar" y protección de la corrección manual); las filas se interpretan
        // con el estado VIVO de la cabecera.
        var viewModel = CreateViewModel();
        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);

        viewModel.LoadStagedInvoice(CreateInvoice(
            CurrencyCodes.BsS,
            CreateLine(1, unitCost: 10m, unitCostDocument: 365m)));

        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);
        Assert.Equal(string.Empty, viewModel.AppliedRateText);
        Assert.Equal(365m, Assert.Single(viewModel.Lines).UnitCostUSD);
    }

    [Fact]
    public void ChangingCurrencyPostStaging_RecalculatesAllRowsInCascade_KeepingDocumentCost()
    {
        // 8.176: cambiar la moneda Bs.S -> USD (y viceversa) conserva el Costo factura extraído y
        // reinterpreta Costo USD / precios sugeridos en TODAS las filas, en bloque.
        var viewModel = CreateViewModel();
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "36.50";
        viewModel.LoadStagedInvoice(CreateInvoice(
            CurrencyCodes.BsS,
            CreateLine(1, unitCost: 10m, unitCostDocument: 365m),
            CreateLine(2, unitCost: 10m, unitCostDocument: 365m) with { ConversionFactor = 2m }));

        var lineA = viewModel.Lines[0];
        var lineB = viewModel.Lines[1];
        Assert.Equal(10m, lineA.UnitCostUSD);
        Assert.Equal(5m, lineB.UnitCostUSD);

        // Bs.S -> USD: Costo USD = Costo factura / Factor
        viewModel.SelectedCurrency = CurrencyCodes.Usd;

        Assert.Equal(365m, lineA.EditUnitCostDocument);
        Assert.Equal(365m, lineB.EditUnitCostDocument);
        Assert.Equal(365m, lineA.UnitCostUSD);
        Assert.Equal(182.5m, lineB.UnitCostUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(365m * 1.2m), lineA.SuggestedRetailPriceUSD);

        // USD -> Bs.S: Costo USD = (Costo factura / Tasa aplicada) / Factor
        viewModel.SelectedCurrency = CurrencyCodes.BsS;

        Assert.Equal(365m, lineA.EditUnitCostDocument);
        Assert.Equal(10m, lineA.UnitCostUSD);
        Assert.Equal(5m, lineB.UnitCostUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(10m * 1.2m), lineA.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.ToBsSCeiling(lineA.SuggestedRetailPriceUSD, 36.5m), lineA.SuggestedRetailPriceBsS);
    }

    [Fact]
    public void ClearingAppliedRateInBsS_ZeroesCostoUsdAndBlocksConfirm_UntilValidRateReturns()
    {
        // 8.176: con Bs.S y tasa en blanco o 0 la grilla no produce NaN/Infinity: Costo USD queda en
        // 0,00 y el botón Confirmar y Procesar Factura se inhabilita hasta una tasa válida.
        var viewModel = CreateViewModel();
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "36.50";
        viewModel.LoadStagedInvoice(CreateInvoice(
            CurrencyCodes.BsS,
            CreateLine(1, unitCost: 10m, unitCostDocument: 365m)));

        var line = Assert.Single(viewModel.Lines);
        Assert.True(viewModel.CanConfirmInvoice);
        Assert.Equal(10m, line.UnitCostUSD);

        viewModel.AppliedRateText = string.Empty;

        Assert.Equal(0m, line.UnitCostUSD);
        Assert.Equal(0m, line.SuggestedRetailPriceUSD);
        Assert.Equal(0m, line.SuggestedWholesalePriceUSD);
        Assert.True(viewModel.ShowRateValidationHint);
        Assert.False(viewModel.CanConfirmInvoice);
        Assert.Equal("Costo factura (Bs.S)", viewModel.DocumentCostHeader);

        viewModel.AppliedRateText = "0";
        Assert.Equal(0m, line.UnitCostUSD);
        Assert.False(viewModel.CanConfirmInvoice);

        viewModel.AppliedRateText = "40";
        Assert.Equal(9.13m, line.UnitCostUSD);
        Assert.False(viewModel.ShowRateValidationHint);
        Assert.True(viewModel.CanConfirmInvoice);
    }

    [Fact]
    public async Task ConfirmInvoiceAsync_AfterCurrencyFlipToUsd_SendsLiveCurrencyWithoutRate()
    {
        // 8.176: si el revisor cambia la moneda a USD después del staging, el confirm transporta la
        // moneda viva (sin tasa: en USD la tasa canónica la impone el backend).
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CurrencyCodes.BsS, CreateLine(1, unitCost: 10m, unitCostDocument: 365m));
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged with { Status = "Applied" });
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "36.50";
        viewModel.LoadStagedInvoice(staged);

        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        await viewModel.ConfirmInvoiceAsync();

        Assert.NotNull(captured);
        Assert.Equal(CurrencyCodes.Usd, captured!.Currency);
        Assert.Null(captured.AppliedRate);
    }

    [Fact]
    public async Task ConfirmInvoiceAsync_WithEditedBsSRate_SendsLiveCurrencyAndRate()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CurrencyCodes.BsS, CreateLine(1, unitCost: 10m, unitCostDocument: 365m));
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged with { Status = "Applied" });
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "36.50";
        viewModel.LoadStagedInvoice(staged);

        viewModel.AppliedRateText = "40";
        await viewModel.ConfirmInvoiceAsync();

        Assert.NotNull(captured);
        Assert.Equal(CurrencyCodes.BsS, captured!.Currency);
        Assert.Equal(40m, captured.AppliedRate);
    }

    [Fact]
    public async Task ConfirmAsync_CurrencySwitchToUsd_ReinterpretsSnapshotAndRecalculatesProductCost()
    {
        // 8.176: confirmar una factura staged en Bs.S con la moneda en USD conmuta el snapshot
        // (tasa canónica 1) y renormaliza el costo USD de la línea y el costo/precio del producto.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-901",
            NormalizedRifOrNit = "J901",
            CommercialName = "Proveedor Switch",
            NormalizedCommercialName = "PROVEEDOR SWITCH"
        };
        var product = new ProductBuilder()
            .WithSku("7590002")
            .WithName("Arroz 1kg")
            .WithCostAndMargin(8m, 25m)
            .WithStock(5m)
            .Build();
        context.Suppliers.Add(supplier);
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var backendService = CreateBackendService(context);
        var staged = await backendService.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            null,
            new[] { new StageLineDto("SUP-2", product.SKU, product.Name, 2m, 730m) },
            CurrencyCodes.BsS,
            36.5m,
            OcrSourced: true));
        Assert.Equal(20m, Assert.Single(staged.Lines).UnitCostUSD);

        var confirmed = await backendService.ConfirmAsync(
            staged.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(staged.Lines[0].Id, true, 25m, 25m) },
                AppliedRate: null,
                Currency: CurrencyCodes.Usd));

        Assert.Equal(CurrencyCodes.Usd, confirmed.Currency);
        Assert.Equal(1m, confirmed.AppliedRate);
        Assert.Equal(730m, Assert.Single(confirmed.Lines).UnitCostUSD);
        var updatedProduct = await context.Products.FindAsync(product.Id);
        Assert.NotNull(updatedProduct);
        Assert.Equal(730m, updatedProduct!.CostPriceUSD);
    }

    [Fact]
    public async Task ConfirmAsync_CurrencySwitchToBsSWithRate_PersistsSnapshotAndRecalculatesCosts()
    {
        // 8.176: confirmar una factura staged en USD con la moneda en Bs.S + tasa conmuta el snapshot
        // y renormaliza (Costo factura / Tasa).
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-902",
            NormalizedRifOrNit = "J902",
            CommercialName = "Proveedor Switch BsS",
            NormalizedCommercialName = "PROVEEDOR SWITCH BSS"
        };
        var product = new ProductBuilder()
            .WithSku("7590003")
            .WithName("Pasta 1kg")
            .WithCostAndMargin(8m, 25m)
            .WithStock(5m)
            .Build();
        context.Suppliers.Add(supplier);
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var backendService = CreateBackendService(context);
        var staged = await backendService.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            null,
            new[] { new StageLineDto("SUP-3", product.SKU, product.Name, 2m, 10m) },
            CurrencyCodes.Usd,
            1m,
            OcrSourced: true));
        Assert.Equal(10m, Assert.Single(staged.Lines).UnitCostUSD);

        var confirmed = await backendService.ConfirmAsync(
            staged.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(staged.Lines[0].Id, true, 25m, 25m) },
                AppliedRate: 36.5m,
                Currency: CurrencyCodes.BsS));

        Assert.Equal(CurrencyCodes.BsS, confirmed.Currency);
        Assert.Equal(36.5m, confirmed.AppliedRate);
        Assert.Equal(0.27m, Assert.Single(confirmed.Lines).UnitCostUSD);
        var updatedProduct = await context.Products.FindAsync(product.Id);
        Assert.NotNull(updatedProduct);
        Assert.Equal(0.27m, updatedProduct!.CostPriceUSD);
    }

    [Fact]
    public async Task ConfirmAsync_CurrencySwitchToBsSWithoutRate_Rejects()
    {
        // 8.176 (zero-trust): cambiar la moneda del documento a Bs.S sin tasa explícita no reutiliza
        // la tasa de otra interpretación; el confirm rechaza la solicitud.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var (backendService, stagedLineId, invoiceId) = await StageSingleUsdLineAsync(context);

        await Assert.ThrowsAsync<ArgumentException>(() => backendService.ConfirmAsync(
            invoiceId,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(stagedLineId, true, 25m, 25m) },
                AppliedRate: null,
                Currency: CurrencyCodes.BsS)));
    }

    [Fact]
    public async Task ConfirmAsync_UnknownCurrency_Rejects()
    {
        // 8.176 (zero-trust): la moneda confirmada solo puede ser USD o Bs.S.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var (backendService, stagedLineId, invoiceId) = await StageSingleUsdLineAsync(context);

        await Assert.ThrowsAsync<ArgumentException>(() => backendService.ConfirmAsync(
            invoiceId,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(stagedLineId, true, 25m, 25m) },
                Currency: "EUR")));
    }

    // ---- helpers (espejo de SupplierInvoiceClientTests) ----

    private static SupplierInvoiceViewModel CreateViewModel(
        ClientSupplierInvoiceService? invoiceService = null,
        IDialogService? dialogService = null,
        IExchangeRateService? exchangeRateService = null)
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        return new SupplierInvoiceViewModel(
            invoiceService ?? Mock.Of<ClientSupplierInvoiceService>(),
            userSession,
            dialogService ?? Mock.Of<IDialogService>(),
            Mock.Of<IFilePickerDialog>(),
            exchangeRateService ?? CreateRateService(0m));
    }

    private static IExchangeRateService CreateRateService(decimal rate)
    {
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(service => service.GetCurrentRateAsync())
            .ReturnsAsync((rate, (DateTime?)null));
        return rateService.Object;
    }

    private static SupplierInvoiceDetailDto CreateInvoice(string currency, params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", currency, currency == CurrencyCodes.BsS ? 36.5m : 1m, lines);

    private static Inventory.Module.Services.SupplierInvoiceService CreateBackendService(InventoryDbContext context) =>
        new(
            context,
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISupplierProductSimilaritySearch>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>());

    private static async Task<(Inventory.Module.Services.SupplierInvoiceService Service, int LineId, int InvoiceId)>
        StageSingleUsdLineAsync(InventoryDbContext context)
    {
        var supplier = new Supplier
        {
            RifOrNit = "J-903",
            NormalizedRifOrNit = "J903",
            CommercialName = "Proveedor Validaciones",
            NormalizedCommercialName = "PROVEEDOR VALIDACIONES"
        };
        var product = new ProductBuilder()
            .WithSku("7590004")
            .WithName("Harina 1kg")
            .WithCostAndMargin(5m, 25m)
            .WithStock(3m)
            .Build();
        context.Suppliers.Add(supplier);
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var service = CreateBackendService(context);
        var staged = await service.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            null,
            new[] { new StageLineDto("SUP-4", product.SKU, product.Name, 1m, 5m) },
            CurrencyCodes.Usd,
            1m,
            OcrSourced: true));
        return (service, Assert.Single(staged.Lines).Id, staged.Id);
    }

    private static SupplierInvoiceLineDto CreateLine(
        int id,
        decimal unitCost = 10m,
        bool isApproved = true,
        int? resolvedProductId = 9,
        string status = "Update",
        decimal? unitCostDocument = null) => new(
            id,
            "SUP-1",
            "12345",
            "Coffee",
            2m,
            unitCostDocument ?? unitCost,
            unitCost,
            status,
            resolvedProductId,
            8m,
            20m,
            10m,
            5m,
            20m,
            10m,
            12m,
            11m,
            isApproved,
            "Barcode",
            null,
            null,
            null);
}
