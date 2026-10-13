using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
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

public sealed class SupplierInvoiceClientTests
{
    [Fact]
    public void DocumentCostHeader_FollowsHeaderSelection_AndLoadDoesNotReassignIt()
    {
        // 8.164/8.178: el encabezado del costo editable acompaña la moneda VIVA de la cabecera;
        // cargar/recargar la factura no re-asigna la selección (aislamiento del flujo "Actualizar"
        // y protección de la corrección manual de la tasa).
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);

        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        Assert.Equal("Costo factura (Bs.S)", viewModel.DocumentCostHeader);

        viewModel.LoadStagedInvoice(CreateInvoice(CurrencyCodes.BsS, CreateLine(1)));
        Assert.Equal("Costo factura (Bs.S)", viewModel.DocumentCostHeader);

        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);

        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));
        Assert.Equal("Costo factura (USD)", viewModel.DocumentCostHeader);
    }

    [Fact]
    public void ToConfirmLine_IncludesConversionFactorPerLine()
    {
        // 8.165: el factor de conversión caja→unidad viaja al confirm por línea (default 1).
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));
        var line = Assert.Single(viewModel.Lines);

        Assert.Equal(1m, line.ConversionFactor);

        line.ConversionFactor = 12m;

        Assert.Equal(12m, line.ToConfirmLine().ConversionFactor);
    }

    [Fact]
    public void StagedLine_UsesDtoConversionFactorForPrefill()
    {
        // 8.166: el factor recordado por proveedor+producto llega al VM por la línea staged
        // (visible y editable en la grilla).
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1) with { ConversionFactor = 12m }));

        Assert.Equal(12m, Assert.Single(viewModel.Lines).ConversionFactor);
    }

    [Fact]
    public void StagedLine_DefaultsApprovalToOn()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));

        Assert.True(Assert.Single(viewModel.Lines).IsApproved);
    }

    [Theory]
    [InlineData("New", "[NUEVO]")]
    [InlineData("Update", "[ACTUALIZAR]")]
    [InlineData("Unchanged", "[UNCHANGED]")]
    [InlineData("Conflict", "[CONFLICT]")]
    public void StatusBadge_MapsBackendStatus(string status, string expected)
    {
        Assert.Equal(expected, SupplierInvoiceLineViewModel.GetStatusLabel(status));
    }

    [Fact]
    public void LiveUnitCostAndComparison_UpdateImmediatelyWhenFactorOrDocumentCostChanges()
    {
        // 8.168: caso real reportado (VELAS BIPA 1X40: costo factura 10.529 USD, factor 40,
        // costo anterior 0.24): Costo USD y Costo anterior → nuevo se recalculan en vivo a 0.2632 (0,26)
        // y cascadan a los precios sugeridos por margen.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        var dto = CreateLine(1, unitCost: 10.529m, unitCostDocument: 10.529m) with
        {
            OldCostPriceUSD = 0.24m,
            MarginRetailOverride = 30m,
            MarginWholesaleOverride = 15m,
            ConversionFactor = 40m
        };
        viewModel.LoadStagedInvoice(CreateInvoice(dto));

        var line = Assert.Single(viewModel.Lines);
        Assert.Equal(0.2632m, line.UnitCostUSD);
        Assert.Equal($"{0.24m:N2} → {0.2632m:N2}", line.CostComparison);
        Assert.Equal(PricingCalculator.RoundPriceUp(0.2632m * 1.30m), line.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(0.2632m * 1.15m), line.SuggestedWholesalePriceUSD);

        line.EditUnitCostDocumentText = "20,00";
        Assert.Equal(20m, line.EditUnitCostDocument);
        Assert.Equal(0.5m, line.UnitCostUSD);
        Assert.Equal($"{0.24m:N2} → {0.5m:N2}", line.CostComparison);
        Assert.Equal(PricingCalculator.RoundPriceUp(0.5m * 1.30m), line.SuggestedRetailPriceUSD);

        line.ConversionFactorText = "10";
        Assert.Equal(10m, line.ConversionFactor);
        Assert.Equal(2m, line.UnitCostUSD);
        Assert.Equal($"{0.24m:N2} → {2m:N2}", line.CostComparison);
    }

    [Fact]
    public void LiveUnitCostInBsS_NormalizesEditedCostAndFactorWithAppliedRate()
    {
        // 8.168: en facturas Bs.S el cálculo en vivo divide por el factor y convierte con la tasa aplicada.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        var dto = CreateLine(1, unitCost: 10m, unitCostDocument: 365m) with
        {
            OldCostPriceUSD = 0.80m
        };
        // 8.178: flujo real: la moneda del documento ya está seleccionada antes de cargar.
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.LoadStagedInvoice(CreateInvoice(CurrencyCodes.BsS, dto));

        var line = Assert.Single(viewModel.Lines);
        Assert.Equal(10m, line.UnitCostUSD);

        line.ConversionFactorText = "10";
        Assert.Equal(1m, line.UnitCostUSD);
        Assert.Equal($"{0.80m:N2} → {1m:N2}", line.CostComparison);
    }

    [Fact]
    public void BsSInvoice_ChangingAppliedRateText_ReactivelyUpdatesUnitCostComparisonAndSuggestedPrices()
    {
        // 8.172: al cambiar la tasa aplicada en Bs.S, todas las filas recalculan en vivo su Costo USD,
        // comparación de costo y precios sugeridos (USD y Bs.S) sin necesidad de editar cada fila.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "36.50";
        var dto = CreateLine(1, unitCost: 10m, unitCostDocument: 365m) with
        {
            OldCostPriceUSD = 8m,
            MarginRetailOverride = 30m,
            MarginWholesaleOverride = 15m
        };
        viewModel.LoadStagedInvoice(CreateInvoice(CurrencyCodes.BsS, dto));

        var line = Assert.Single(viewModel.Lines);
        Assert.Equal(10m, line.UnitCostUSD);
        Assert.Equal(13m, line.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.ToBsSCeiling(13m, 36.50m), line.SuggestedRetailPriceBsS);

        viewModel.AppliedRateText = "73,00";

        Assert.Equal(73m, line.AppliedRate);
        Assert.Equal(5m, line.UnitCostUSD);
        Assert.Equal($"{8m:N2} → {5m:N2}", line.CostComparison);
        Assert.Equal(PricingCalculator.RoundPriceUp(5m * 1.30m), line.SuggestedRetailPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(5m * 1.15m), line.SuggestedWholesalePriceUSD);
        Assert.Equal(PricingCalculator.ToBsSCeiling(line.SuggestedRetailPriceUSD, 73m), line.SuggestedRetailPriceBsS);
        Assert.Equal(PricingCalculator.ToBsSCeiling(line.SuggestedWholesalePriceUSD, 73m), line.SuggestedWholesalePriceBsS);
    }

    [Fact]
    public async Task ScanInvoice_InBsSCurrency_WhenOcrDetectsExchangeRate_ReplacesDisplayedRateAndStagesWithDetectedRate()
    {
        // 8.172: cuando la factura está en Bs.S y el OCR detecta la tasa impresa en el documento,
        // reemplaza la tasa mostrada en la sección (incluso si ya tenía la tasa BCV precargada) y la usa al stagear.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        StageSupplierInvoiceRequestDto? captured = null;
        var extracted = new OcrExtractionResultDto(
            new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 784.50m, 92m, 90m, 88m) },
            "J-123",
            "Proveedor OCR",
            "0238317",
            78.45m);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(extracted);
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<StageSupplierInvoiceRequestDto, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new SupplierInvoiceDetailDto(
                42,
                7,
                "Draft",
                CurrencyCodes.BsS,
                78.45m,
                new[] { CreateLine(1, unitCost: 10m, unitCostDocument: 784.50m) }));

        var viewModel = CreateViewModel(
            service.Object,
            exchangeRateService: CreateRateService(36.50m),
            filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, CurrencyCodes.BsS);
        Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);
        Assert.Equal("36.50", viewModel.AppliedRateText);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Equal(78.45m, viewModel.OcrDetectedExchangeRate);
        Assert.Equal("78.45", viewModel.AppliedRateText);
        Assert.NotNull(captured);
        Assert.Equal(CurrencyCodes.BsS, captured!.Currency);
        Assert.Equal(78.45m, captured.AppliedRate);
        Assert.Equal(10m, Assert.Single(viewModel.Lines).UnitCostUSD);
    }

    [Fact]
    public async Task ScanInvoice_WithUsdSelection_WhenOcrDetectsExchangeRate_SwitchesToBsSAndKeepsAmountsReactiveToRateEdits()
    {
        // 8.174: la tasa impresa evidencia una factura en Bs.S. Sin moneda recordada en el proveedor
        // (selector en USD), el escaneo ajusta la moneda a Bs.S, aplica la tasa detectada y el
        // revisor puede corregirla después con montos de grilla reactivos antes de confirmar.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        StageSupplierInvoiceRequestDto? captured = null;
        var extracted = new OcrExtractionResultDto(
            new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 784.50m, 92m, 90m, 88m) },
            "J-123",
            "Proveedor OCR",
            "0238317",
            78.45m);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(extracted);
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<StageSupplierInvoiceRequestDto, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new SupplierInvoiceDetailDto(
                42,
                7,
                "Draft",
                CurrencyCodes.BsS,
                78.45m,
                new[] { CreateLine(1, unitCost: 10m, unitCostDocument: 784.50m) with { OldCostPriceUSD = 10m } }));

        var viewModel = CreateViewModel(
            service.Object,
            exchangeRateService: CreateRateService(36.50m),
            filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, null);
        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
        Assert.Equal(string.Empty, viewModel.AppliedRateText);
        Assert.False(viewModel.IsRateInputEnabled);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);
        Assert.True(viewModel.IsRateInputEnabled);
        Assert.Equal("78.45", viewModel.AppliedRateText);
        Assert.Contains("Moneda ajustada a Bs.S", viewModel.StatusMessage);
        Assert.NotNull(captured);
        Assert.Equal(CurrencyCodes.BsS, captured!.Currency);
        Assert.Equal(78.45m, captured.AppliedRate);

        var stagedLine = Assert.Single(viewModel.Lines);
        Assert.Equal(10m, stagedLine.UnitCostUSD);

        viewModel.AppliedRateText = "36,50";

        Assert.Equal(36.50m, viewModel.StagedInvoice!.AppliedRate);
        Assert.Equal(21.49m, stagedLine.UnitCostUSD);
        Assert.Equal($"{10m:N2} → {21.49m:N2}", stagedLine.CostComparison);
    }

    [Fact]
    public async Task ScanInvoice_WithUsdSelection_WithoutDetectedExchangeRate_KeepsUsdAndCanonicalRateOne()
    {
        // 8.174 (guardia): sin tasa impresa el escaneo no cambia la moneda seleccionada ni la tasa.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        StageSupplierInvoiceRequestDto? captured = null;
        var extracted = new OcrExtractionResultDto(
            new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 10m, 92m, 90m, 88m) },
            "J-123",
            "Proveedor OCR",
            "0238317");
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(extracted);
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<StageSupplierInvoiceRequestDto, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new SupplierInvoiceDetailDto(
                42,
                7,
                "Draft",
                CurrencyCodes.Usd,
                1m,
                new[] { CreateLine(1) }));

        var viewModel = CreateViewModel(
            service.Object,
            exchangeRateService: CreateRateService(36.50m),
            filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, null);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Null(viewModel.OcrDetectedExchangeRate);
        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
        Assert.Equal(string.Empty, viewModel.AppliedRateText);
        Assert.DoesNotContain("Moneda ajustada", viewModel.StatusMessage);
        Assert.NotNull(captured);
        Assert.Equal(CurrencyCodes.Usd, captured!.Currency);
        Assert.Equal(1m, captured.AppliedRate);
    }

    [Fact]
    public async Task ConfirmAsync_BsSInvoiceWithUpdatedAppliedRate_RecalculatesProductCostAndPrice()
    {
        // 8.172: si el revisor ajusta la tasa aplicada en Bs.S durante la revisión, el confirm
        // actualiza la tasa de la factura y recalcula el costo en USD y los precios del producto.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-555",
            NormalizedRifOrNit = "J555",
            CommercialName = "Proveedor BsS",
            NormalizedCommercialName = "PROVEEDOR BSS"
        };
        var product = new ProductBuilder()
            .WithSku("7590001")
            .WithName("Café Molido")
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
            new[] { new StageLineDto("SUP-1", product.SKU, product.Name, 2m, 800m) },
            CurrencyCodes.BsS,
            40m,
            OcrSourced: true));
        Assert.Equal(20m, Assert.Single(staged.Lines).UnitCostUSD);

        var confirmed = await backendService.ConfirmAsync(
            staged.Id,
            new ConfirmSupplierInvoiceRequestDto(
                new[] { new ConfirmLineDto(staged.Lines[0].Id, true, 25m, 25m) },
                AppliedRate: 80m));

        Assert.Equal(80m, confirmed.AppliedRate);
        Assert.Equal(10m, Assert.Single(confirmed.Lines).UnitCostUSD);
        var updatedProduct = await context.Products.FindAsync(product.Id);
        Assert.NotNull(updatedProduct);
        Assert.Equal(10m, updatedProduct!.CostPriceUSD);
        Assert.Equal(PricingCalculator.RoundPriceUp(10m * 1.25m), updatedProduct.PriceRetailUSD);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void InvalidOrEmptyConversionFactor_UsesSafeFallbackOneAndFlagsInvalidState(string invalidFactorInput)
    {
        // 8.168: nunca divide por cero ni produce NaN/Infinity mientras el usuario borra o edita el factor.
        var line = new SupplierInvoiceLineViewModel(CreateLine(1, unitCost: 12m, unitCostDocument: 12m));

        line.ConversionFactorText = invalidFactorInput;

        Assert.True(line.HasInvalidConversionFactor);
        Assert.Equal(1m, line.EffectiveConversionFactor);
        Assert.Equal(12m, line.UnitCostUSD);
        Assert.Equal(1m, line.ToConfirmLine().ConversionFactor);
    }

    [Fact]
    public void FlexibleDecimalTextBuffers_PreserveTransitoryInputAndAcceptCommaOrDot()
    {
        // 8.168: escribir ".0", "10.", "10," o 3-4 decimales no pisa el texto transitorio ni falla.
        var line = new SupplierInvoiceLineViewModel(CreateLine(1, unitCost: 10.529m, unitCostDocument: 10.529m));
        Assert.Equal("10.529", line.EditUnitCostDocumentText);

        line.EditUnitCostDocumentText = ".0";
        Assert.Equal(".0", line.EditUnitCostDocumentText);
        Assert.Equal(0m, line.EditUnitCostDocument);

        line.EditUnitCostDocumentText = "10.";
        Assert.Equal("10.", line.EditUnitCostDocumentText);
        Assert.Equal(10m, line.EditUnitCostDocument);

        line.EditUnitCostDocumentText = "10,5295";
        Assert.Equal("10,5295", line.EditUnitCostDocumentText);
        Assert.Equal(10.5295m, line.EditUnitCostDocument);

        line.EditQuantityText = "3,25";
        Assert.Equal(3.25m, line.EditQuantity);
    }

    [Fact]
    public void MasterApprovalCheckbox_TogglesAllApprovableLinesAndReflectsIndeterminateState()
    {
        // 8.168: el checkbox maestro en el encabezado de "Aprobar" marca/desmarca todas las líneas
        // aprobables e ignora las líneas nuevas no resueltas.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(
            CreateLine(1, isApproved: true, resolvedProductId: 10),
            CreateLine(2, isApproved: true, resolvedProductId: 11),
            CreateLine(3, isApproved: false, resolvedProductId: null, status: "New")));

        Assert.True(viewModel.HasApprovableLines);
        Assert.Equal(true, viewModel.AreAllApprovableLinesApproved);

        viewModel.Lines[0].IsApproved = false;
        Assert.Null(viewModel.AreAllApprovableLinesApproved);

        viewModel.AreAllApprovableLinesApproved = false;
        Assert.Equal(false, viewModel.AreAllApprovableLinesApproved);
        Assert.False(viewModel.Lines[0].IsApproved);
        Assert.False(viewModel.Lines[1].IsApproved);
        Assert.False(viewModel.CanConfirmInvoice);

        viewModel.AreAllApprovableLinesApproved = true;
        Assert.Equal(true, viewModel.AreAllApprovableLinesApproved);
        Assert.True(viewModel.Lines[0].IsApproved);
        Assert.True(viewModel.Lines[1].IsApproved);
        Assert.False(viewModel.Lines[2].IsApproved);
        Assert.True(viewModel.CanConfirmInvoice);
    }

    [Fact]
    public void HasSelectedFilePath_IsTrueOnlyWhenTabularPathIsPresentAndNotOcr()
    {
        // 8.168: el campo de ruta de archivo se oculta cuando está vacío o en flujo OCR.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        Assert.False(viewModel.HasSelectedFilePath);

        viewModel.SelectedFilePath = InvoiceFilePath;
        Assert.True(viewModel.HasSelectedFilePath);

        viewModel.IsOcrSource = true;
        Assert.False(viewModel.HasSelectedFilePath);

        viewModel.IsOcrSource = false;
        viewModel.SelectedFilePath = string.Empty;
        Assert.False(viewModel.HasSelectedFilePath);
    }

    [Fact]
    public void UnresolvedLine_ShowsNuevoBadgeAndIsCreationCandidate()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1, resolvedProductId: null, status: "Conflict")));

        var line = Assert.Single(viewModel.Lines);

        Assert.Equal("[NUEVO]", line.StatusLabel);
        Assert.False(line.CanApprove);
        Assert.True(line.CanCreateProduct);
    }

    [Fact]
    public void ResolvedLine_IsApprovableAndNotCreationCandidate()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));

        var line = Assert.Single(viewModel.Lines);

        Assert.True(line.CanApprove);
        Assert.False(line.CanCreateProduct);
    }

    [Fact]
    public void BsSInvoice_ExposesDocumentCostAndCurrencyFlag()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        // 8.178: la cabecera es del revisor; en el flujo real el selector ya está en Bs.S antes de cargar.
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.LoadStagedInvoice(CreateInvoice(
            CurrencyCodes.BsS,
            CreateLine(1, unitCost: 10m, unitCostDocument: 365m)));

        Assert.True(viewModel.IsBsSInvoice);
        Assert.Equal(365m, Assert.Single(viewModel.Lines).UnitCostDocument);
    }

    [Fact]
    public void CanStageInvoice_RequiresPositiveRateForBsSCurrency()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        PrepareStageableInvoice(viewModel);

        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency);
        Assert.False(viewModel.IsBsSCurrency);
        Assert.True(viewModel.CanStageInvoice);

        viewModel.SelectedCurrency = CurrencyCodes.BsS;

        Assert.True(viewModel.IsBsSCurrency);
        Assert.Equal(string.Empty, viewModel.AppliedRateText);
        Assert.False(viewModel.CanStageInvoice);
        Assert.True(viewModel.ShowRateValidationHint);

        viewModel.AppliedRateText = "36,50";

        Assert.True(viewModel.CanStageInvoice);
        Assert.False(viewModel.ShowRateValidationHint);

        viewModel.AppliedRateText = "0";

        Assert.False(viewModel.CanStageInvoice);
        Assert.True(viewModel.ShowRateValidationHint);
    }

    [Fact]
    public void SwitchingToBsSCurrency_PrefillsOfficialRate()
    {
        var viewModel = CreateViewModel(
            Mock.Of<ClientSupplierInvoiceService>(),
            exchangeRateService: CreateRateService(36.50m));

        viewModel.SelectedCurrency = CurrencyCodes.BsS;

        Assert.True(viewModel.IsBsSCurrency);
        Assert.Equal("36.50", viewModel.AppliedRateText);
    }

    [Theory]
    [InlineData("36,50", 36.50)]
    [InlineData("36.50", 36.50)]
    [InlineData("3.650,50", 3650.50)]
    public async Task StageInvoiceAsync_BsSCurrency_SendsParsedAppliedRate(string rateText, decimal expectedRate)
    {
        var request = await CaptureStageRequestAsync(CurrencyCodes.BsS, rateText);

        Assert.Equal(CurrencyCodes.BsS, request.Currency);
        Assert.Equal(expectedRate, request.AppliedRate);
    }

    [Fact]
    public void SwitchingBackToBsSCurrency_DoesNotOverrideUserEditedRate()
    {
        var viewModel = CreateViewModel(
            Mock.Of<ClientSupplierInvoiceService>(),
            exchangeRateService: CreateRateService(36.50m));

        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.AppliedRateText = "40";

        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        viewModel.SelectedCurrency = CurrencyCodes.BsS;

        Assert.Equal("40", viewModel.AppliedRateText);
    }

    [Fact]
    public async Task StageInvoiceAsync_UsdCurrency_SendsCanonicalRateOne()
    {
        var request = await CaptureStageRequestAsync(CurrencyCodes.Usd, "999");

        Assert.Equal(CurrencyCodes.Usd, request.Currency);
        Assert.Equal(1m, request.AppliedRate);
    }

    [Fact]
    public async Task StageAsync_BsSCurrency_SerializesCurrencyAndAppliedRateInJsonBody()
    {
        var invoice = CreateInvoice(CurrencyCodes.BsS, CreateLine(1));
        var handler = new StubHttpMessageHandler(JsonResponse(invoice));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        await service.StageAsync(new StageSupplierInvoiceRequestDto(
            7,
            "J-123",
            "Supplier",
            null,
            new[] { new StageLineDto("SUP-1", "12345", "Coffee", 2m, 365m) },
            CurrencyCodes.BsS,
            36.50m));

        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("Bs.S", payload.RootElement.GetProperty("currency").GetString());
        Assert.Equal(36.50m, payload.RootElement.GetProperty("appliedRate").GetDecimal());
    }

    [Fact]
    public async Task CreateProductCommand_OnSuccess_ReloadsResolvedLine()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var invoice = CreateInvoice(CreateLine(1, resolvedProductId: null, status: "New"));
        var refreshed = CreateInvoice(CreateLine(1));
        CreateInvoiceProductRequestDto? captured = null;
        service.Setup(client => client.CreateProductFromLineAsync(
                invoice.Id,
                1,
                It.IsAny<CreateInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, CreateInvoiceProductRequestDto, CancellationToken>((_, _, request, _) => captured = request)
            .ReturnsAsync(refreshed);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(dialogVm =>
            {
                dialogVm.Barcode = "7591234567890";
                dialogVm.Name = "Harina P.A.N. 1kg";
            })
            .Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(invoice);

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        Assert.NotNull(captured);
        Assert.Equal("7591234567890", captured!.Barcode);
        Assert.Equal("Harina P.A.N. 1kg", captured.Name);
        var line = Assert.Single(viewModel.Lines);
        Assert.True(line.CanApprove);
        Assert.False(line.CanCreateProduct);
        Assert.Equal("[ACTUALIZAR]", line.StatusLabel);
    }

    [Fact]
    public async Task CreateProductCommand_OnFailure_ShowsErrorAndKeepsLineUnresolved()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var invoice = CreateInvoice(CreateLine(1, resolvedProductId: null, status: "New"));
        service.Setup(client => client.CreateProductFromLineAsync(
                invoice.Id,
                1,
                It.IsAny<CreateInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("El SKU 7591234567890 ya existe en el catálogo (409)."));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(invoice);

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        dialog.Verify(
            service => service.ShowError(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        var line = Assert.Single(viewModel.Lines);
        Assert.True(line.CanCreateProduct);
        Assert.Equal("[NUEVO]", line.StatusLabel);
    }

    [Fact]
    public async Task CreateProductCommand_ForResolvedLine_DoesNotOpenDialog()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var dialog = new Mock<IDialogService>();
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        dialog.Verify(
            service => service.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()),
            Times.Never);
    }

    [Fact]
    public void SavedMapping_AppliesAutomaticallyAndCanBeOverridden()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.SelectedSupplier = new SupplierSummaryDto(
            7,
            "J-123",
            "Supplier",
            new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost"));

        viewModel.SetFileHeaders(new[] { "EAN", "Vendor Code", "Item", "Units", "Cost", "Alternate Cost" });

        var unitCostMapping = Assert.Single(viewModel.ColumnMappings, mapping => mapping.FieldKey == "UnitCost");
        Assert.Equal("Cost", unitCostMapping.SelectedColumnName);

        unitCostMapping.SelectedColumnName = "Alternate Cost";

        Assert.Equal("Alternate Cost", unitCostMapping.SelectedColumnName);
    }

    [Fact]
    public void SelectedSupplier_WithMissingSavedColumns_KeepsSpecificWarning()
    {
        // 8.161: la seleccion ocurre en el combo (listado completo); la advertencia por columnas
        // guardadas ausentes se conserva al aplicar el mapping del proveedor seleccionado.
        var savedMapping = new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost");
        var supplier = new SupplierSummaryDto(7, "J-123", "Supplier", savedMapping);
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.SetFileHeaders(new[] { "EAN", "Vendor Code", "Item", "Units" });

        viewModel.SelectedSupplier = supplier;

        Assert.Contains("no aparecen en el archivo", viewModel.StatusMessage);
    }

    [Fact]
    public void SelectedSupplier_PrefillsRememberedInvoiceCurrency()
    {
        // 8.167: al seleccionar un proveedor con moneda recordada, la vista la prefija; sin
        // moneda recordada NO se pisa la elección vigente.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null, "Bs.S");
        Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);

        viewModel.SelectedSupplier = new SupplierSummaryDto(8, "J-124", "Otro", null, null);
        Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);
    }

    [Fact]
    public async Task LoadSuppliers_PopulatesSelectorAndPreservesSelectionById()
    {
        // 8.161: al abrir la vista se lista todo el catalogo de proveedores y la seleccion vigente
        // se preserva por Id (instancias nuevas del listado).
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.GetSuppliersAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SupplierSummaryDto(1, "J-1", "Proveedor Alfa", null),
                new SupplierSummaryDto(2, "J-2", "Proveedor Beta", null)
            });
        var viewModel = CreateViewModel(service.Object);
        viewModel.SelectedSupplier = new SupplierSummaryDto(2, "J-2", "Proveedor Beta", null);

        await viewModel.LoadSuppliersCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.Suppliers.Count);
        Assert.Equal(2, viewModel.SelectedSupplier?.Id);
        Assert.Same(viewModel.Suppliers[1], viewModel.SelectedSupplier);
        service.VerifyAll();
    }

    [Fact]
    public async Task OpenRegisterSupplier_WithConfirmedDialog_RegistersAndSelects()
    {
        // 8.161: el boton "Registrar proveedor" abre el modal; al confirmar, el proveedor se
        // registra, se agrega al selector y queda seleccionado.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.CreateSupplierAsync(
                It.Is<CreateSupplierRequestDto>(request =>
                    request.RifOrNit == "J-777" && request.CommercialName == "Nuevo Proveedor"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SupplierSummaryDto(9, "J-777", "Nuevo Proveedor", null));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(dialogService => dialogService.ShowRegisterSupplierDialog(It.IsAny<RegisterSupplierDialogViewModel>()))
            .Callback<RegisterSupplierDialogViewModel>(dialogViewModel =>
            {
                dialogViewModel.RifOrNit = "J-777";
                dialogViewModel.CommercialName = "Nuevo Proveedor";
            })
            .Returns(true);
        var viewModel = CreateViewModel(service.Object, dialogService: dialog.Object);

        await viewModel.OpenRegisterSupplierCommand.ExecuteAsync(null);

        Assert.Equal(9, viewModel.SelectedSupplier?.Id);
        Assert.Contains(viewModel.Suppliers, supplier => supplier.Id == 9);
        Assert.Contains("registrado", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        service.VerifyAll();
    }

    [Fact]
    public async Task OpenRegisterSupplier_WithCancelledDialog_DoesNotRegister()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(dialogService => dialogService.ShowRegisterSupplierDialog(It.IsAny<RegisterSupplierDialogViewModel>()))
            .Returns(false);
        var viewModel = CreateViewModel(service.Object, dialogService: dialog.Object);

        await viewModel.OpenRegisterSupplierCommand.ExecuteAsync(null);

        service.VerifyNoOtherCalls();
        Assert.Null(viewModel.SelectedSupplier);
    }

    [Fact]
    public async Task ParseFileWithMappingAsync_ParsesXlsxRows()
    {
        var service = CreateClientService();
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Invoice");
        worksheet.Cell(1, 1).Value = "Barcode";
        worksheet.Cell(1, 2).Value = "Supplier Code";
        worksheet.Cell(1, 3).Value = "Name";
        worksheet.Cell(1, 4).Value = "Quantity";
        worksheet.Cell(1, 5).Value = "Unit Cost";
        worksheet.Cell(2, 1).Value = "12345";
        worksheet.Cell(2, 2).Value = "SUP-1";
        worksheet.Cell(2, 3).Value = "Coffee";
        worksheet.Cell(2, 4).Value = 2m;
        worksheet.Cell(2, 5).Value = 4.25m;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;

        var lines = await service.ParseFileWithMappingAsync(stream, ".xlsx", Mapping());

        var line = Assert.Single(lines);
        Assert.Equal("12345", line.Barcode);
        Assert.Equal("SUP-1", line.SupplierCode);
        Assert.Equal("Coffee", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(4.25m, line.UnitCostDocument);
    }

    [Fact]
    public async Task ParseFileWithMappingAsync_ParsesSemicolonCsvWithDecimalComma()
    {
        var service = CreateClientService();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Barcode;Supplier Code;Name;Quantity;Unit Cost\n12345;SUP-1;Coffee;2,5;4,25"));

        var lines = await service.ParseFileWithMappingAsync(stream, ".csv", Mapping());

        var line = Assert.Single(lines);
        Assert.Equal(2.5m, line.Quantity);
        Assert.Equal(4.25m, line.UnitCostDocument);
    }

    [Fact]
    public async Task ParseFileWithMappingAsync_HeaderOnlyCsv_RejectsFile()
    {
        var service = CreateClientService();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Barcode;Supplier Code;Name;Quantity;Unit Cost"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ParseFileWithMappingAsync(stream, ".csv", Mapping()));
    }

    [Fact]
    public async Task ParseFileWithMappingAsync_ParsesXmlLineElements()
    {
        var service = CreateClientService();
        const string xml = "<Invoice><Lines><Line><Barcode>12345</Barcode><SupplierCode>SUP-1</SupplierCode><Name>Coffee</Name><Quantity>3</Quantity><UnitCost>5.50</UnitCost></Line></Lines></Invoice>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var lines = await service.ParseFileWithMappingAsync(
            stream,
            ".xml",
            new SupplierColumnMappingDto("Barcode", "SupplierCode", "Name", "Quantity", "UnitCost"));

        var line = Assert.Single(lines);
        Assert.Equal("12345", line.Barcode);
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(5.50m, line.UnitCostDocument);
    }

    [Fact]
    public async Task ParseFileWithMappingAsync_ParsesDotThousandsWithEsVeConvention()
    {
        // 8.147-T9/D2: punto(s) con grupos de exactamente 3 dígitos = miles es-VE ("4.250" → 4250);
        // con coma y punto presentes gana el último como decimal; punto con 1–2 o 4+ dígitos queda decimal.
        var service = CreateClientService();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "Barcode;Supplier Code;Name;Quantity;Unit Cost\n" +
            "1;SUP-1;Miles;4.250;1.234.567\n" +
            "2;SUP-2;Decimales;4.25;4.2500\n" +
            "3;SUP-3;Ambos;1.234,56;1,234.56\n" +
            "4;SUP-4;CeroInicial;0.500;0.614\n" +
            "5;SUP-5;Comilla;“72.00;1,50"));

        var lines = await service.ParseFileWithMappingAsync(stream, ".csv", Mapping());

        Assert.Equal(5, lines.Count);
        Assert.Equal(4250m, lines[0].Quantity);
        Assert.Equal(1234567m, lines[0].UnitCostDocument);
        Assert.Equal(4.25m, lines[1].Quantity);
        Assert.Equal(4.25m, lines[1].UnitCostDocument);
        Assert.Equal(1234.56m, lines[2].Quantity);
        Assert.Equal(1234.56m, lines[2].UnitCostDocument);
        // 8.163: cero inicial = decimal ("0.500" → 0,5) y comillas tipográficas de OCR fuera.
        Assert.Equal(0.5m, lines[3].Quantity);
        Assert.Equal(0.614m, lines[3].UnitCostDocument);
        Assert.Equal(72.00m, lines[4].Quantity);
        Assert.Equal(1.50m, lines[4].UnitCostDocument);
    }

    [Fact]
    public async Task ConfirmInvoiceAsync_SendsOnlyApprovedRowsWithMarginOverrides()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        ConfirmSupplierInvoiceRequestDto? capturedRequest = null;
        var stagedInvoice = CreateInvoice(CreateLine(10), CreateLine(11));
        service.Setup(client => client.ConfirmAsync(
                stagedInvoice.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => capturedRequest = request)
            .ReturnsAsync(stagedInvoice);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(stagedInvoice);
        var approved = viewModel.Lines[0];
        approved.MarginRetailOverride = 27.5m;
        approved.MarginWholesaleOverride = 12.25m;
        viewModel.Lines[1].IsApproved = false;

        await viewModel.ConfirmInvoiceAsync();

        var confirmation = Assert.Single(Assert.IsType<ConfirmSupplierInvoiceRequestDto>(capturedRequest).Lines);
        Assert.Equal(approved.LineId, confirmation.LineId);
        Assert.True(confirmation.IsApproved);
        Assert.Equal(27.5m, confirmation.MarginRetailOverride);
        Assert.Equal(12.25m, confirmation.MarginWholesaleOverride);
        service.Verify(client => client.ConfirmAsync(
            stagedInvoice.Id,
            It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ServiceMethods_UseSupplierLookupAndInvoiceRoutes()
    {
        var mapping = new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost");
        var supplier = new SupplierSummaryDto(7, "J-123", "Supplier", mapping);
        var invoice = CreateInvoice(CreateLine(1));
        var handler = new StubHttpMessageHandler(
            JsonResponse(new[] { supplier }),
            JsonResponse(supplier),
            JsonResponse(invoice),
            JsonResponse(invoice),
            JsonResponse(invoice));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var suppliers = await service.GetSuppliersAsync("J-123", null);
        await service.CreateSupplierAsync(new CreateSupplierRequestDto("J-123", "Supplier"));
        await service.StageAsync(new StageSupplierInvoiceRequestDto(
            supplier.Id,
            supplier.RifOrNit,
            supplier.CommercialName,
            mapping,
            new[] { new StageLineDto("SUP-1", "12345", "Coffee", 2m, 4m) },
            CurrencyCodes.Usd,
            1m));
        await service.GetInvoiceAsync(invoice.Id);
        await service.ConfirmAsync(
            invoice.Id,
            new ConfirmSupplierInvoiceRequestDto(new[] { new ConfirmLineDto(1, true, 25m, 10m) }));

        Assert.Equal(mapping, Assert.Single(suppliers).ColumnMapping);
        Assert.Equal(
            new[]
            {
                "/api/suppliers?rif=J-123",
                "/api/suppliers",
                "/api/supplier-invoices",
                $"/api/supplier-invoices/{invoice.Id}",
                $"/api/supplier-invoices/{invoice.Id}/confirm"
            },
            handler.RequestPaths);
    }

    [Fact]
    public async Task CreateProductFromLineAsync_PostsBarcodeAndNameToCreateProductRoute()
    {
        var invoice = CreateInvoice(CreateLine(1));
        var handler = new StubHttpMessageHandler(JsonResponse(invoice));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var result = await service.CreateProductFromLineAsync(
            invoice.Id,
            15,
            new CreateInvoiceProductRequestDto("7591234567890", "Harina P.A.N. 1kg"));

        Assert.Equal(invoice.Id, result.Id);
        Assert.Equal(
            $"/api/supplier-invoices/{invoice.Id}/lines/15/create-product",
            Assert.Single(handler.RequestPaths));
        using var payload = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("7591234567890", payload.RootElement.GetProperty("barcode").GetString());
        Assert.Equal("Harina P.A.N. 1kg", payload.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SupplierLookup_IncludesPersistedColumnMapping()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-123",
            NormalizedRifOrNit = "J123",
            CommercialName = "Mapped Supplier",
            NormalizedCommercialName = "MAPPED SUPPLIER",
            ColumnMapping = new SupplierColumnMapping
            {
                BarcodeColumnName = "EAN",
                SupplierCodeColumnName = "Vendor Code",
                NameColumnName = "Item",
                QuantityColumnName = "Units",
                UnitCostColumnName = "Cost"
            }
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        var service = CreateBackendService(context);

        var result = await service.GetSuppliersAsync("J-123", null);

        Assert.Equal(
            new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost"),
            Assert.Single(result).ColumnMapping);
    }

    [Fact]
    public async Task SupplierLookup_IncludesRememberedInvoiceCurrency()
    {
        // 8.167: el listado de proveedores expone la moneda de facturación recordada.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var supplier = new Supplier
        {
            RifOrNit = "J-777",
            NormalizedRifOrNit = "J777",
            CommercialName = "Currency Supplier",
            NormalizedCommercialName = "CURRENCY SUPPLIER",
            InvoiceCurrency = "Bs.S"
        };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        var service = CreateBackendService(context);

        var result = await service.GetSuppliersAsync("J-777", null);

        Assert.Equal("Bs.S", Assert.Single(result).InvoiceCurrency);
    }

    [Fact]
    public async Task GetSuppliersAsync_WithoutFilters_ListsAllFromRootRoute()
    {
        // 8.161: sin filtros el cliente consulta el listado completo (sin query string).
        var supplier = new SupplierSummaryDto(1, "J-1", "Proveedor Alfa", null);
        var handler = new StubHttpMessageHandler(JsonResponse(new[] { supplier }));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var suppliers = await service.GetSuppliersAsync(null, null);

        Assert.Equal("/api/suppliers", Assert.Single(handler.RequestPaths));
        Assert.Equal(1, Assert.Single(suppliers).Id);
    }

    [Fact]
    public async Task GetSuppliersAsync_WithoutFilters_ReturnsOrderedFullListing()
    {
        // 8.161: el backend sin filtros devuelve el catalogo completo ordenado por nombre
        // (selector de la vista), sin exigir RIF/nombre.
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        context.Suppliers.Add(new Supplier { CommercialName = "Zeta", NormalizedCommercialName = "ZETA" });
        context.Suppliers.Add(new Supplier { CommercialName = "Alfa", NormalizedCommercialName = "ALFA" });
        await context.SaveChangesAsync();
        var service = CreateBackendService(context);

        var result = await service.GetSuppliersAsync(null, null);

        Assert.Equal(new[] { "Alfa", "Zeta" }, result.Select(supplier => supplier.CommercialName));
    }

    [Fact]
    public async Task CreateSupplierAsync_ReturnsNullMappingWhenNoTemplateExists()
    {
        using var context = TestDatabaseFactory.CreateInventoryDbContext();
        var service = CreateBackendService(context);

        var supplier = await service.CreateSupplierAsync(new CreateSupplierRequestDto("J-456", "New Supplier"));

        Assert.Null(supplier.ColumnMapping);
    }

    [Fact]
    public async Task ExtractOcrAsync_PostsMultipartFileWithSupplierAndFullMappingFields()
    {
        var fileBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x7F, 0xAB };
        var filePath = WriteTempOcrFile(fileBytes, ".png");
        try
        {
            var extracted = new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 91.5m, 88m, 77.1m) },
                "J-123",
                "Proveedor OCR",
                "0238317");
            var handler = new MultipartCaptureHandler(JsonResponse(extracted));
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
            var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

            var result = await service.ExtractOcrAsync(
                filePath,
                7,
                new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost"));

            Assert.Equal("J-123", result.DetectedRif);
            Assert.Equal("0238317", result.DetectedInvoiceNumber);
            Assert.Equal("/api/supplier-invoices/ocr-extract", Assert.Single(handler.RequestPaths));
            Assert.Equal("7", handler.GetFieldValue("supplierId"));
            Assert.Equal("Item", handler.GetFieldValue("nameColumn"));
            Assert.Equal("Units", handler.GetFieldValue("quantityColumn"));
            Assert.Equal("Cost", handler.GetFieldValue("unitCostColumn"));
            Assert.Equal("Vendor Code", handler.GetFieldValue("supplierCodeColumn"));
            Assert.Equal("EAN", handler.GetFieldValue("barcodeColumn"));
            Assert.Equal(Path.GetFileName(filePath), handler.GetFileName("file"));
            var uploadedBytes = handler.GetPartBytes("file");
            Assert.NotNull(uploadedBytes);
            Assert.Equal(fileBytes, uploadedBytes);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractOcrAsync_WithoutSupplierOrMapping_OmitsOptionalFields()
    {
        var filePath = WriteTempOcrFile(new byte[] { 1, 2, 3 }, ".pdf");
        try
        {
            var handler = new MultipartCaptureHandler(
                JsonResponse(new OcrExtractionResultDto([], null, null, null)));
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
            var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

            await service.ExtractOcrAsync(filePath, null, null);

            Assert.True(handler.HasPart("file"));
            Assert.False(handler.HasPart("supplierId"));
            Assert.False(handler.HasPart("nameColumn"));
            Assert.False(handler.HasPart("quantityColumn"));
            Assert.False(handler.HasPart("unitCostColumn"));
            Assert.False(handler.HasPart("supplierCodeColumn"));
            Assert.False(handler.HasPart("barcodeColumn"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ExtractOcrAsync_MappingWithoutOptionalColumns_OmitsNullMappingFields()
    {
        var filePath = WriteTempOcrFile(new byte[] { 9, 8, 7 }, ".jpg");
        try
        {
            var handler = new MultipartCaptureHandler(
                JsonResponse(new OcrExtractionResultDto([], null, null, null)));
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
            var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

            await service.ExtractOcrAsync(
                filePath,
                7,
                new SupplierColumnMappingDto(null, null, "Item", "Units", "Cost"));

            Assert.Equal("Item", handler.GetFieldValue("nameColumn"));
            Assert.Equal("Units", handler.GetFieldValue("quantityColumn"));
            Assert.Equal("Cost", handler.GetFieldValue("unitCostColumn"));
            Assert.False(handler.HasPart("supplierCodeColumn"));
            Assert.False(handler.HasPart("barcodeColumn"));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ScanInvoice_HappyPath_StagesOcrSourcedLinesAndExposesReviewState()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        StageSupplierInvoiceRequestDto? captured = null;
        var extracted = new OcrExtractionResultDto(
            new[]
            {
                new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 91.5m, 88m, 77.1m),
                new OcrExtractedLineDto(null, null, "Sugar", null, null, 55.5m, 0m, 0m)
            },
            "J-123",
            "Proveedor OCR",
            "0238317");
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, Mapping(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(extracted);
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<StageSupplierInvoiceRequestDto, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateInvoice(CreateLine(1)));
        var viewModel = CreateViewModel(service.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", Mapping());

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.True(captured!.OcrSourced);
        Assert.Null(captured.ColumnMapping);
        Assert.Equal(7, captured.SupplierId);
        Assert.Equal(2, captured.Lines.Count);
        Assert.Equal(2m, captured.Lines[0].Quantity);
        Assert.Equal(4.25m, captured.Lines[0].UnitCostDocument);
        Assert.Equal(91.5m, captured.Lines[0].OcrNameConfidence);
        Assert.Equal(88m, captured.Lines[0].OcrQuantityConfidence);
        Assert.Equal(77.1m, captured.Lines[0].OcrUnitCostConfidence);
        Assert.Equal(0m, captured.Lines[1].Quantity);
        Assert.Equal(0m, captured.Lines[1].UnitCostDocument);
        Assert.Equal(55.5m, captured.Lines[1].OcrNameConfidence);
        Assert.True(viewModel.IsOcrSource);
        Assert.Equal("J-123", viewModel.OcrDetectedRif);
        Assert.Equal("Proveedor OCR", viewModel.OcrDetectedSupplierName);
        Assert.Equal("0238317", viewModel.OcrDetectedInvoiceNumber);
    }

    [Fact]
    public async Task ScanInvoice_UnreadableRow_BlocksStagingWithExactMessage()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, Mapping(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[]
                {
                    new OcrExtractedLineDto(null, null, "Coffee", 2m, 4.25m, 90m, 80m, 70m),
                    new OcrExtractedLineDto(" ", null, null, 1m, 1m, 0m, 0m, 0m)
                },
                null,
                null,
                null));
        var dialog = new Mock<IDialogService>();
        var viewModel = CreateViewModel(service.Object, dialog.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", Mapping());

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Equal(
            "Hay filas OCR sin nombre ni código; complete al menos un identificador antes de cargar.",
            viewModel.StatusMessage);
        dialog.Verify(
            service => service.ShowError(
                "Filas OCR incompletas",
                "Hay filas OCR sin nombre ni código; complete al menos un identificador antes de cargar."),
            Times.Once);
        Assert.Null(viewModel.StagedInvoice);
        Assert.False(viewModel.IsOcrSource);
        Assert.Null(viewModel.OcrDetectedInvoiceNumber);
        service.Verify(client => client.ExtractOcrAsync(
            OcrFilePath, 7, Mapping(), It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScanInvoice_PrefillsSupplierFieldsFromHintsOnlyWhenEmpty()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 90m, 80m, 70m) },
                "J-999",
                "Proveedor Detectado",
                null));
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateInvoice(CreateLine(1)));
        var viewModel = CreateViewModel(service.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);
        viewModel.SupplierRifOrNit = string.Empty;
        viewModel.SupplierCommercialName = string.Empty;

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Equal("J-999", viewModel.SupplierRifOrNit);
        Assert.Equal("Proveedor Detectado", viewModel.SupplierCommercialName);
    }

    [Fact]
    public async Task ScanInvoice_DoesNotOverrideNonEmptySupplierFieldsWithHints()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 90m, 80m, 70m) },
                "J-999",
                "Proveedor Detectado",
                null));
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateInvoice(CreateLine(1)));
        var viewModel = CreateViewModel(service.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);
        viewModel.SupplierRifOrNit = "J-888";
        viewModel.SupplierCommercialName = "Nombre Manual";

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Equal("J-888", viewModel.SupplierRifOrNit);
        Assert.Equal("Nombre Manual", viewModel.SupplierCommercialName);
        Assert.Equal("J-999", viewModel.OcrDetectedRif);
        Assert.Equal("Proveedor Detectado", viewModel.OcrDetectedSupplierName);
    }

    [Fact]
    public async Task ScanInvoice_WithNoExtractedLines_ShowsActionableGuidance()
    {
        // 8.163: sin líneas extraíbles el mensaje guía a re-tomar la foto en vez de dejar al
        // operador sin contexto.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(Array.Empty<OcrExtractedLineDto>(), null, null, null));
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowWarning("Sin líneas OCR", It.IsAny<string>()));
        var viewModel = CreateViewModel(service.Object, dialogService: dialog.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);

        Assert.Contains("enfoque", viewModel.StatusMessage);
        dialog.Verify(d => d.ShowWarning("Sin líneas OCR", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmSuccess_ClearsOcrReviewState()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var staged = CreateInvoice(CreateLine(1));
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 90m, 80m, 70m) },
                null,
                null,
                "0238317"));
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(staged);
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(staged);
        var dialog = new Mock<IDialogService>();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var viewModel = CreateViewModel(service.Object, dialog.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsOcrSource);
        Assert.Equal("0238317", viewModel.OcrDetectedInvoiceNumber);

        await viewModel.ConfirmInvoiceAsync();

        Assert.False(viewModel.IsOcrSource);
        Assert.Null(viewModel.OcrDetectedRif);
        Assert.Null(viewModel.OcrDetectedSupplierName);
        Assert.Null(viewModel.OcrDetectedInvoiceNumber);
    }

    [Fact]
    public async Task TabularFileSelection_ClearsOcrReviewState()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 90m, 80m, 70m) },
                "J-123",
                "Proveedor OCR",
                "0238317"));
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateInvoice(CreateLine(1)));
        service.Setup(client => client.ReadHeadersAsync(
                TabularFilePath,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Name", "Quantity", "Unit Cost" });
        var viewModel = CreateViewModel(service.Object, filePicker: PickerReturning(OcrFilePath));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsOcrSource);
        Assert.Equal("0238317", viewModel.OcrDetectedInvoiceNumber);

        await viewModel.LoadFileAsync(TabularFilePath);

        Assert.False(viewModel.IsOcrSource);
        Assert.Null(viewModel.OcrDetectedRif);
        Assert.Null(viewModel.OcrDetectedSupplierName);
        Assert.Null(viewModel.OcrDetectedInvoiceNumber);
    }

    [Fact]
    public async Task StageInvoiceAsync_TabularFlow_KeepsMappingAndOcrSourcedFalse()
    {
        var request = await CaptureStageRequestAsync(CurrencyCodes.Usd, null);

        Assert.False(request.OcrSourced);
        Assert.NotNull(request.ColumnMapping);
        Assert.Equal("Name", request.ColumnMapping!.NameColumnName);
        Assert.Equal("Quantity", request.ColumnMapping.QuantityColumnName);
        Assert.Equal("Unit Cost", request.ColumnMapping.UnitCostColumnName);
    }

    [Fact]
    public void Dispose_ClearsLinesAndColumnMappings()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var viewModel = CreateViewModel(service.Object);
        viewModel.SetFileHeaders(new[] { "Barcode", "Name", "Quantity", "Unit Cost" });
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1)));

        Assert.NotEmpty(viewModel.ColumnMappings);
        Assert.NotEmpty(viewModel.Lines);

        viewModel.Dispose();

        Assert.Empty(viewModel.ColumnMappings);
        Assert.Empty(viewModel.Lines);
    }

    [Fact]
    public void SupplierControllers_EnforceSecurityStampValidationAndOcrUploadSizeLimits()
    {
        Assert.NotNull(Attribute.GetCustomAttribute(
            typeof(Backend.API.Controllers.SupplierInvoicesController),
            typeof(Backend.API.Attributes.RequireSecurityStampValidationAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(
            typeof(Backend.API.Controllers.SuppliersController),
            typeof(Backend.API.Attributes.RequireSecurityStampValidationAttribute)));

        var ocrMethod = typeof(Backend.API.Controllers.SupplierInvoicesController)
            .GetMethod(nameof(Backend.API.Controllers.SupplierInvoicesController.OcrExtractAsync));
        Assert.NotNull(ocrMethod);
        var sizeLimit = Attribute.GetCustomAttribute(
            ocrMethod!,
            typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute)) as Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute;
        var formLimits = Attribute.GetCustomAttribute(
            ocrMethod!,
            typeof(Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute)) as Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute;
        Assert.NotNull(sizeLimit);
        Assert.NotNull(formLimits);
        Assert.Equal(Inventory.Module.Services.Ocr.OcrExtractionService.MaxFileBytes + 4L * 1024 * 1024, formLimits!.MultipartBodyLengthLimit);
    }

    private const string InvoiceFilePath = "C:\\facturas\\factura.csv";
    private const string OcrFilePath = "C:\\facturas\\factura-escaneada.png";
    private const string TabularFilePath = "C:\\facturas\\factura.xlsx";

    private static SupplierInvoiceViewModel CreateViewModel(
        ClientSupplierInvoiceService invoiceService,
        IDialogService? dialogService = null,
        IExchangeRateService? exchangeRateService = null,
        IFilePickerDialog? filePicker = null)
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        return new SupplierInvoiceViewModel(
            invoiceService,
            userSession,
            dialogService ?? Mock.Of<IDialogService>(),
            filePicker ?? Mock.Of<IFilePickerDialog>(),
            exchangeRateService ?? CreateRateService(0m));
    }

    private static IFilePickerDialog PickerReturning(string? filePath)
    {
        var picker = new Mock<IFilePickerDialog>();
        picker.Setup(dialog => dialog.PickFilePath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(filePath);
        return picker.Object;
    }

    private static string WriteTempOcrFile(byte[] bytes, string extension)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ocr-upload-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(filePath, bytes);
        return filePath;
    }

    private static IExchangeRateService CreateRateService(decimal rate)
    {
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(service => service.GetCurrentRateAsync())
            .ReturnsAsync((rate, (DateTime?)null));
        return rateService.Object;
    }

    [Fact]
    public async Task UnresolvedLine_AllowsModifyingAllFieldsAndPreservesEditsAcrossCreateProductAndConfirm()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var unresolvedLine = CreateLine(1, unitCost: 10m, isApproved: false, resolvedProductId: null, status: "New", unitCostDocument: 365m);
        var existingLine = CreateLine(2, unitCost: 5m, isApproved: true, resolvedProductId: 20, status: "Update", unitCostDocument: 182.5m);
        var staged = CreateInvoice(CurrencyCodes.BsS, unresolvedLine, existingLine);
        var refreshedAfterCreate = CreateInvoice(
            CurrencyCodes.BsS,
            CreateLine(1, unitCost: 10m, isApproved: false, resolvedProductId: 99, status: "Update", unitCostDocument: 365m),
            existingLine);

        CreateInvoiceProductDialogViewModel? capturedDialogVm = null;
        ConfirmSupplierInvoiceRequestDto? capturedConfirm = null;

        service.Setup(client => client.CreateProductFromLineAsync(
                staged.Id,
                1,
                It.IsAny<CreateInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedAfterCreate);
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, req, _) => capturedConfirm = req)
            .ReturnsAsync(refreshedAfterCreate with { Status = "Applied" });

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(vm =>
            {
                capturedDialogVm = vm;
                vm.Barcode = "7591234567890";
            })
            .Returns(true);
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        // 8.178: flujo real: la moneda del documento ya está seleccionada antes de cargar.
        viewModel.SelectedCurrency = CurrencyCodes.BsS;
        viewModel.LoadStagedInvoice(staged);

        var line1 = viewModel.Lines[0];
        var line2 = viewModel.Lines[1];

        // Unresolved product has editor enabled even when IsOcrSource is false
        Assert.True(line1.IsEditorEnabled);
        Assert.False(line2.IsEditorEnabled);

        // Modify fields on the nonexistent product BEFORE clicking "Crear Producto"
        line1.EditName = "Harina P.A.N. Amarilla 1kg";
        line1.EditQuantityText = "6";
        line1.EditUnitCostDocumentText = "730";
        line1.ConversionFactorText = "2";
        line1.MarginRetailOverride = 30m;
        line1.MarginWholesaleOverride = 15m;

        // Also modify factor/margins on the second line to ensure sibling edits are preserved
        line2.ConversionFactorText = "4";
        line2.MarginRetailOverride = 25m;

        await viewModel.CreateProductCommand.ExecuteAsync(line1);

        Assert.NotNull(capturedDialogVm);
        Assert.Equal("Harina P.A.N. Amarilla 1kg", capturedDialogVm!.Name);
        Assert.Contains("Bs.S 730", capturedDialogVm.CostInfo);
        Assert.Contains("6", capturedDialogVm.CostInfo);

        var reloadedLine1 = viewModel.Lines[0];
        var reloadedLine2 = viewModel.Lines[1];

        // Edits are preserved and line1 remains editable and approved after creating the product
        Assert.True(reloadedLine1.IsEditorEnabled);
        Assert.True(reloadedLine1.IsApproved);
        Assert.Equal("Harina P.A.N. Amarilla 1kg", reloadedLine1.EditName);
        Assert.Equal(6m, reloadedLine1.EditQuantity);
        Assert.Equal(730m, reloadedLine1.EditUnitCostDocument);
        Assert.Equal(2m, reloadedLine1.ConversionFactor);
        Assert.Equal(30m, reloadedLine1.MarginRetailOverride);
        Assert.Equal(15m, reloadedLine1.MarginWholesaleOverride);
        Assert.Equal(4m, reloadedLine2.ConversionFactor);
        Assert.Equal(25m, reloadedLine2.MarginRetailOverride);

        // Further modify quantity after product creation and confirm
        reloadedLine1.EditQuantityText = "8";
        await viewModel.ConfirmInvoiceAsync();

        Assert.NotNull(capturedConfirm);
        var confirmLine1 = Assert.Single(capturedConfirm!.Lines, l => l.LineId == 1);
        Assert.Equal("Harina P.A.N. Amarilla 1kg", confirmLine1.Name);
        Assert.Equal(8m, confirmLine1.Quantity);
        Assert.Equal(730m, confirmLine1.UnitCostDocument);
        Assert.Equal(2m, confirmLine1.ConversionFactor);
        Assert.Equal(30m, confirmLine1.MarginRetailOverride);
        Assert.Equal(15m, confirmLine1.MarginWholesaleOverride);
    }

    [Fact]
    public async Task CreateProductCommand_ModeA_LinkExistingProduct_SearchesPredictivelyAssociatesProductUpdatesStatusAndRecalculatesCostComparison()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var unresolvedLine = CreateLine(1, unitCost: 10.50m, isApproved: true, resolvedProductId: null, status: "New", unitCostDocument: 10.50m) with
        {
            OldCostPriceUSD = null
        };
        var staged = CreateInvoice(unresolvedLine);
        var catalogMatch = new ProductDto
        {
            Id = 45,
            SKU = "7591234000011",
            Name = "Harina P.A.N. 1kg",
            CostPriceUSD = 8.20m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m,
            StockQuantity = 24m
        };
        var refreshedAfterLink = CreateInvoice(
            CreateLine(1, unitCost: 10.50m, isApproved: true, resolvedProductId: 45, status: "Update", unitCostDocument: 10.50m) with
            {
                Name = catalogMatch.Name,
                OldCostPriceUSD = catalogMatch.CostPriceUSD,
                OldProfitMarginRetail = catalogMatch.ProfitMarginRetail,
                OldProfitMarginWholesale = catalogMatch.ProfitMarginWholesale,
                MarginRetailOverride = catalogMatch.ProfitMarginRetail,
                MarginWholesaleOverride = catalogMatch.ProfitMarginWholesale
            });

        LinkInvoiceProductRequestDto? capturedLink = null;
        service.Setup(client => client.SearchCatalogProductsAsync("Harina", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { catalogMatch });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                1,
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, req, _) => capturedLink = req)
            .ReturnsAsync(refreshedAfterLink);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(vm =>
            {
                Assert.True(vm.IsLinkExistingMode);
                vm.SearchQuery = "Harina";
                var match = Assert.Single(vm.SearchResults);
                vm.SelectedProduct = match;
                vm.SaveCommand.Execute(null);
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        Assert.NotNull(capturedLink);
        Assert.Equal(45, capturedLink!.ProductId);

        var linkedLine = Assert.Single(viewModel.Lines);
        Assert.Equal(45, linkedLine.ResolvedProductId);
        Assert.Equal("Update", linkedLine.Status);
        Assert.Equal("[ACTUALIZAR]", linkedLine.StatusLabel);
        Assert.Equal(8.20m, linkedLine.OldCostPriceUSD);
        Assert.Equal($"{8.20m:N2} → {10.50m:N2}", linkedLine.CostComparison);

        // Live recalculation of CostComparison when document cost changes
        linkedLine.EditUnitCostDocumentText = "12.00";
        Assert.Equal($"{8.20m:N2} → {12.00m:N2}", linkedLine.CostComparison);
    }

    [Fact]
    public async Task CreateProductCommand_ModeA_MultiProductLink_ExpandsRowsAndPropagatesCostAndFactorReactively()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var unresolvedLine = CreateLine(1, unitCost: 24.00m, isApproved: true, resolvedProductId: null, status: "New", unitCostDocument: 24.00m) with
        {
            SupplierCode = "PROV-REFRESCO-CAJA",
            Name = "REFRESCO SURTIDO CAJA",
            Quantity = 5m,
            OldCostPriceUSD = null
        };
        var staged = CreateInvoice(unresolvedLine);

        var productA = new ProductDto
        {
            Id = 101,
            SKU = "7591000001011",
            Name = "Refresco Cola 2L",
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m,
            StockQuantity = 10m
        };
        var productB = new ProductDto
        {
            Id = 102,
            SKU = "7591000001028",
            Name = "Refresco Naranja 2L",
            CostPriceUSD = 1.40m,
            ProfitMarginRetail = 25m,
            ProfitMarginWholesale = 15m,
            StockQuantity = 4m
        };

        var refreshedAfterMultiLink = CreateInvoice(
            CreateLine(1, unitCost: 24.00m, isApproved: true, resolvedProductId: 101, status: "Update", unitCostDocument: 24.00m) with
            {
                SupplierCode = "PROV-REFRESCO-CAJA",
                Name = productA.Name,
                Quantity = 5m,
                MatchMethod = "SupplierCode",
                OldCostPriceUSD = productA.CostPriceUSD,
                OldProfitMarginRetail = productA.ProfitMarginRetail,
                OldProfitMarginWholesale = productA.ProfitMarginWholesale,
                MarginRetailOverride = productA.ProfitMarginRetail,
                MarginWholesaleOverride = productA.ProfitMarginWholesale
            },
            CreateLine(2, unitCost: 24.00m, isApproved: true, resolvedProductId: 102, status: "Update", unitCostDocument: 24.00m) with
            {
                SupplierCode = "PROV-REFRESCO-CAJA",
                Name = productB.Name,
                Quantity = 0m,
                MatchMethod = "SupplierCode",
                OldCostPriceUSD = productB.CostPriceUSD,
                OldProfitMarginRetail = productB.ProfitMarginRetail,
                OldProfitMarginWholesale = productB.ProfitMarginWholesale,
                MarginRetailOverride = productB.ProfitMarginRetail,
                MarginWholesaleOverride = productB.ProfitMarginWholesale
            });

        LinkInvoiceProductRequestDto? capturedLink = null;
        service.Setup(client => client.SearchCatalogProductsAsync("Refresco", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { productA, productB });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                1,
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, req, _) => capturedLink = req)
            .ReturnsAsync(refreshedAfterMultiLink);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(vm =>
            {
                vm.SearchQuery = "Refresco";
                Assert.Equal(2, vm.SelectableSearchResults.Count);
                vm.SelectableSearchResults[0].IsSelected = true;
                vm.SelectableSearchResults[1].IsSelected = true;
                Assert.Equal(2, vm.SelectedProducts.Count);
                vm.SaveCommand.Execute(null);
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        Assert.NotNull(capturedLink);
        Assert.Equal(101, capturedLink!.ProductId);
        Assert.Equal(new[] { 101, 102 }, capturedLink.ProductIds);

        Assert.Equal(2, viewModel.Lines.Count);
        var lineA = viewModel.Lines[0];
        var lineB = viewModel.Lines[1];

        Assert.Equal(5m, lineA.EditQuantity);
        Assert.Equal(0m, lineB.EditQuantity);
        Assert.True(lineA.IsLinkedGroupMember);
        Assert.True(lineB.IsLinkedGroupMember);

        // Editing ConversionFactor on lineA propagates reactively to lineB
        lineA.ConversionFactorText = "12";
        Assert.Equal(12m, lineB.ConversionFactor);
        Assert.Equal(2.00m, lineA.UnitCostUSD);
        Assert.Equal(2.00m, lineB.UnitCostUSD);
        Assert.Equal($"{1.50m:N2} → {2.00m:N2}", lineA.CostComparison);
        Assert.Equal($"{1.40m:N2} → {2.00m:N2}", lineB.CostComparison);

        // Editing EditUnitCostDocumentText on lineB propagates reactively back to lineA
        lineB.EditUnitCostDocumentText = "36.00";
        Assert.Equal(36.00m, lineA.EditUnitCostDocument);
        Assert.Equal(3.00m, lineA.UnitCostUSD);
        Assert.Equal(3.00m, lineB.UnitCostUSD);
    }

    [Fact]
    public async Task CreateProductCommand_ModeA_ReactiveMargins_CalculatesLivePricesAppliesBulkValidatesCoherenceAndPropagatesToStaging()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var unresolvedLine = CreateLine(1, unitCost: 10.00m, isApproved: true, resolvedProductId: null, status: "New", unitCostDocument: 10.00m) with
        {
            SupplierCode = "PROV-BEBIDA-SURTIDA",
            Name = "BEBIDA SABORES SURTIDOS",
            Quantity = 4m,
            OldCostPriceUSD = null
        };
        var staged = CreateInvoice(unresolvedLine);

        var productA = new ProductDto
        {
            Id = 201,
            SKU = "7592000002011",
            Name = "Bebida Uva 1.5L",
            CostPriceUSD = 8.00m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m,
            HasWholesale = true,
            StockQuantity = 12m
        };
        var productB = new ProductDto
        {
            Id = 202,
            SKU = "7592000002028",
            Name = "Bebida Piña 1.5L",
            CostPriceUSD = 7.80m,
            ProfitMarginRetail = 25m,
            ProfitMarginWholesale = 12m,
            HasWholesale = true,
            StockQuantity = 6m
        };

        var refreshedAfterMultiLink = CreateInvoice(
            CreateLine(1, unitCost: 10.00m, isApproved: true, resolvedProductId: 201, status: "Update", unitCostDocument: 10.00m) with
            {
                SupplierCode = "PROV-BEBIDA-SURTIDA",
                Name = productA.Name,
                Quantity = 4m,
                MatchMethod = "SupplierCode",
                OldCostPriceUSD = productA.CostPriceUSD,
                OldProfitMarginRetail = productA.ProfitMarginRetail,
                OldProfitMarginWholesale = productA.ProfitMarginWholesale,
                MarginRetailOverride = productA.ProfitMarginRetail,
                MarginWholesaleOverride = productA.ProfitMarginWholesale
            },
            CreateLine(2, unitCost: 10.00m, isApproved: true, resolvedProductId: 202, status: "Update", unitCostDocument: 10.00m) with
            {
                SupplierCode = "PROV-BEBIDA-SURTIDA",
                Name = productB.Name,
                Quantity = 0m,
                MatchMethod = "SupplierCode",
                OldCostPriceUSD = productB.CostPriceUSD,
                OldProfitMarginRetail = productB.ProfitMarginRetail,
                OldProfitMarginWholesale = productB.ProfitMarginWholesale,
                MarginRetailOverride = productB.ProfitMarginRetail,
                MarginWholesaleOverride = productB.ProfitMarginWholesale
            });

        LinkInvoiceProductRequestDto? capturedLink = null;
        service.Setup(client => client.SearchCatalogProductsAsync("Bebida", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { productA, productB });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                1,
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, req, _) => capturedLink = req)
            .ReturnsAsync(refreshedAfterMultiLink);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(vm =>
            {
                // 1. Calculation base context in modal header
                Assert.Equal(10.00m, vm.LineUnitCostUSD);
                Assert.Contains("$10", vm.LineUnitCostHeaderText);

                vm.SearchQuery = "Bebida";
                vm.SelectableSearchResults[0].IsSelected = true;
                vm.SelectableSearchResults[1].IsSelected = true;
                Assert.Equal(2, vm.SelectedProductMargins.Count);

                var marginA = vm.SelectedProductMargins[0];
                var marginB = vm.SelectedProductMargins[1];

                // 2. Initial current margins & live suggested prices loaded from ProductDto
                Assert.Equal(30m, marginA.MarginRetail);
                Assert.Equal(15m, marginA.MarginWholesale);
                Assert.Equal(13.00m, marginA.SuggestedRetailPriceUSD);
                Assert.Equal(11.50m, marginA.SuggestedWholesalePriceUSD);

                // 3. Smooth decimal typing (with comma and trailing separator) + reactive calculation
                marginA.MarginRetailText = "35,";
                Assert.Equal("35,", marginA.MarginRetailText);
                Assert.Equal(35m, marginA.MarginRetail);
                Assert.Equal(13.50m, marginA.SuggestedRetailPriceUSD);

                marginA.MarginRetailText = "35,5";
                Assert.Equal(35.5m, marginA.MarginRetail);
                Assert.Equal(PricingCalculator.RoundPriceUp(10.00m * 1.355m), marginA.SuggestedRetailPriceUSD);

                // 4. Commercial coherence warning when Wholesale Margin > Retail Margin
                marginA.MarginWholesaleText = "40";
                Assert.True(marginA.HasMarginCoherenceWarning);
                Assert.True(vm.HasAnyMarginCoherenceWarning);
                Assert.NotEmpty(marginA.MarginCoherenceWarningMessage);

                // Fix wholesale margin and verify warning clears
                marginA.MarginWholesaleText = "20";
                Assert.False(marginA.HasMarginCoherenceWarning);
                Assert.False(vm.HasAnyMarginCoherenceWarning);

                // 5. Bulk margin application copies to all selected products, then allows independent edit
                vm.BulkMarginRetailText = "40";
                vm.BulkMarginWholesaleText = "20";
                vm.ApplyBulkMarginsCommand.Execute(null);

                Assert.Equal(40m, marginA.MarginRetail);
                Assert.Equal(20m, marginA.MarginWholesale);
                Assert.Equal(14.00m, marginA.SuggestedRetailPriceUSD);
                Assert.Equal(12.00m, marginA.SuggestedWholesalePriceUSD);

                Assert.Equal(40m, marginB.MarginRetail);
                Assert.Equal(20m, marginB.MarginWholesale);
                Assert.Equal(14.00m, marginB.SuggestedRetailPriceUSD);
                Assert.Equal(12.00m, marginB.SuggestedWholesalePriceUSD);

                // Independent edit on second product after bulk apply
                marginB.MarginRetailText = "50";
                marginB.MarginWholesaleText = "25";
                Assert.Equal(40m, marginA.MarginRetail);
                Assert.Equal(50m, marginB.MarginRetail);
                Assert.Equal(15.00m, marginB.SuggestedRetailPriceUSD);
                Assert.Equal(12.50m, marginB.SuggestedWholesalePriceUSD);

                vm.SaveCommand.Execute(null);
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.CreateProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        // 6. Propagation to LinkInvoiceProductRequestDto and Staging Table [UPDATE] rows
        Assert.NotNull(capturedLink);
        Assert.NotNull(capturedLink!.MarginOverrides);
        Assert.Equal(2, capturedLink.MarginOverrides!.Count);
        Assert.Equal(new LinkedProductMarginOverrideDto(201, 40m, 20m), capturedLink.MarginOverrides[0]);
        Assert.Equal(new LinkedProductMarginOverrideDto(202, 50m, 25m), capturedLink.MarginOverrides[1]);

        Assert.Equal(2, viewModel.Lines.Count);
        var stagedLineA = viewModel.Lines[0];
        var stagedLineB = viewModel.Lines[1];

        Assert.Equal(40m, stagedLineA.MarginRetailOverride);
        Assert.Equal(20m, stagedLineA.MarginWholesaleOverride);
        Assert.Equal(14.00m, stagedLineA.SuggestedRetailPriceUSD);
        Assert.Equal(12.00m, stagedLineA.SuggestedWholesalePriceUSD);

        Assert.Equal(50m, stagedLineB.MarginRetailOverride);
        Assert.Equal(25m, stagedLineB.MarginWholesaleOverride);
        Assert.Equal(15.00m, stagedLineB.SuggestedRetailPriceUSD);
        Assert.Equal(12.50m, stagedLineB.SuggestedWholesalePriceUSD);
    }

    [Fact]
    public async Task ConfirmInvoiceAsync_WithUncheckedNewProductRow_IgnoresUnresolvedRowAndProcessesRemainingLines()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var resolvedLine = CreateLine(1, unitCost: 10m, isApproved: true, resolvedProductId: 10, status: "Update");
        var newUnlinkedLine = CreateLine(2, unitCost: 5m, isApproved: true, resolvedProductId: null, status: "New");
        var staged = CreateInvoice(resolvedLine, newUnlinkedLine);

        ConfirmSupplierInvoiceRequestDto? capturedConfirm = null;
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, req, _) => capturedConfirm = req)
            .ReturnsAsync(staged with { Status = "Applied" });

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        // Initially, newUnlinkedLine has IsApproved = true, so Confirm blocks with warning
        await viewModel.ConfirmInvoiceAsync();
        Assert.Null(capturedConfirm);
        dialog.Verify(d => d.ShowWarning("Productos nuevos sin resolver", It.IsAny<string>()), Times.Once);

        // User unchecks Aprobar on the new/unlinked product row to omit it
        viewModel.Lines[1].IsApproved = false;

        await viewModel.ConfirmInvoiceAsync();

        Assert.NotNull(capturedConfirm);
        var confirmedLine = Assert.Single(capturedConfirm!.Lines);
        Assert.Equal(1, confirmedLine.LineId);
        Assert.True(confirmedLine.IsApproved);
    }

    [Fact]
    public async Task LinkProductToLineAsync_AndSearchCatalogProductsAsync_UseExpectedClientRoutes()
    {
        var invoice = CreateInvoice(CreateLine(1));
        var pagedProducts = new PagedResultDto<ProductDto>
        {
            Items = new List<ProductDto> { new() { Id = 45, SKU = "7591234", Name = "Harina" } },
            TotalCount = 1
        };

        var handler = new StubHttpMessageHandler(
            JsonResponse(pagedProducts),
            JsonResponse(invoice));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var found = await service.SearchCatalogProductsAsync("Harina PAN");
        var updated = await service.LinkProductToLineAsync(
            invoice.Id,
            15,
            new LinkInvoiceProductRequestDto(45, "HARINA PAN 1KG"));

        Assert.Equal(45, Assert.Single(found).Id);
        Assert.Equal(invoice.Id, updated.Id);
        Assert.Equal(
            new[]
            {
                "/api/products?filter=Harina%20PAN&page=1&pageSize=20&statusFilter=active",
                $"/api/supplier-invoices/{invoice.Id}/lines/15/link-product"
            },
            handler.RequestPaths);
    }

    [Fact]
    public async Task GetCatalogProductsByIdsAsync_RequestsEachIdAndSkipsMissing()
    {
        // 8.175-S6: la hidratación del modal de actualización consulta GET api/products/{id} por id
        // (ruta existente), deduplicando ids y saltando los que no resuelven (404) sin abortar los demás.
        var product = new ProductDto { Id = 1, Name = "Harina", SKU = "7591234" };
        var handler = new StubHttpMessageHandler(
            JsonResponse(product),
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var found = await service.GetCatalogProductsByIdsAsync(new[] { 1, 2, 1, 2 });

        Assert.Equal(1, Assert.Single(found).Id);
        Assert.Equal(new[] { "/api/products/1", "/api/products/2" }, handler.RequestPaths);
    }

    [Fact]
    public async Task LinkProductToLineAsync_SerializesReplacedLineIds()
    {
        // 8.175-S5: la re-resolución reemplaza el grupo 1:N previo; las filas absorbidas viajan en el body.
        var invoice = CreateInvoice(CreateLine(1));
        var handler = new StubHttpMessageHandler(JsonResponse(invoice));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        await service.LinkProductToLineAsync(
            invoice.Id,
            1,
            new LinkInvoiceProductRequestDto(45, "HARINA PAN 1KG", ReplacedLineIds: new[] { 7, 8 }));

        Assert.Contains("\"replacedLineIds\":[7,8]", Assert.Single(handler.RequestBodies));
    }

    [Fact]
    public async Task UpdateLinkedProduct_GroupMemberClick_OpensHydratedDialogAnchoredToPrimaryAndReLinksWithReplacedIds()
    {
        // 8.175-S1/S2/S4/S7: clic en [ACTUALIZAR] de un miembro del grupo 1:N abre el modal
        // existente hidratado con el/los producto(s) vinculado(s) anclado a la fila primaria;
        // al guardar, la re-resolución viaja con los ids de las filas reemplazadas.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var primaryLine = CreateLine(1, unitCost: 24m, resolvedProductId: 101, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            Name = "REFRESCO SURTIDO CAJA",
            MatchMethod = "SupplierCode",
            MarginRetailOverride = 30m,
            MarginWholesaleOverride = 15m
        };
        var memberLine = CreateLine(2, unitCost: 24m, resolvedProductId: 102, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            Name = "REFRESCO NARANJA CAJA",
            Quantity = 0m,
            MatchMethod = "SupplierCode",
            MarginRetailOverride = 25m,
            MarginWholesaleOverride = 12m
        };
        var untouchedLine = CreateLine(3, unitCost: 5m, resolvedProductId: 30, status: "Unchanged") with
        {
            SupplierCode = "PROV-OTRO",
            Name = "Sin tocar"
        };
        var staged = CreateInvoice(primaryLine, memberLine, untouchedLine);

        var productA = new ProductDto
        {
            Id = 101,
            SKU = "7591000001011",
            Name = "Refresco Cola 2L",
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m,
            StockQuantity = 10m
        };
        var productB = new ProductDto
        {
            Id = 102,
            SKU = "7591000001028",
            Name = "Refresco Naranja 2L",
            CostPriceUSD = 1.40m,
            ProfitMarginRetail = 25m,
            ProfitMarginWholesale = 12m,
            StockQuantity = 4m
        };

        var refreshedAfterReLink = CreateInvoice(
            primaryLine with
            {
                Name = productA.Name,
                OldCostPriceUSD = productA.CostPriceUSD,
                OldProfitMarginRetail = productA.ProfitMarginRetail,
                OldProfitMarginWholesale = productA.ProfitMarginWholesale,
                MarginRetailOverride = 40m,
                MarginWholesaleOverride = 20m
            },
            memberLine with
            {
                Name = productB.Name,
                OldCostPriceUSD = productB.CostPriceUSD,
                OldProfitMarginRetail = productB.ProfitMarginRetail,
                OldProfitMarginWholesale = productB.ProfitMarginWholesale
            },
            untouchedLine);

        IReadOnlyList<int>? requestedProductIds = null;
        LinkInvoiceProductRequestDto? capturedLink = null;
        var capturedLinkLineId = 0;
        service.Setup(client => client.GetCatalogProductsByIdsAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<int>, CancellationToken>((productIds, _) => requestedProductIds = productIds)
            .ReturnsAsync(new[] { productA, productB });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                It.IsAny<int>(),
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, lineId, request, _) =>
            {
                capturedLinkLineId = lineId;
                capturedLink = request;
            })
            .ReturnsAsync(refreshedAfterReLink);

        CreateInvoiceProductDialogViewModel? capturedDialog = null;
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(dialogViewModel =>
            {
                capturedDialog = dialogViewModel;
                Assert.True(dialogViewModel.IsUpdateMode);
                Assert.Equal(new[] { 101, 102 }, dialogViewModel.SelectedProducts.Select(product => product.Id).ToArray());
                Assert.Equal(101, dialogViewModel.SelectedProduct?.Id);
                Assert.Equal(2, dialogViewModel.SelectedProductMargins.Count);
                Assert.Equal(30m, dialogViewModel.SelectedProductMargins[0].MarginRetail);
                Assert.Equal(15m, dialogViewModel.SelectedProductMargins[0].MarginWholesale);
                Assert.Equal(25m, dialogViewModel.SelectedProductMargins[1].MarginRetail);
                Assert.Equal(12m, dialogViewModel.SelectedProductMargins[1].MarginWholesale);

                // 8.175-S4: el revisor ajusta los márgenes del producto primario en vivo.
                dialogViewModel.SelectedProductMargins[0].MarginRetail = 40m;
                dialogViewModel.SelectedProductMargins[0].MarginWholesale = 20m;
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        var primaryRow = viewModel.Lines[0];
        var memberRow = viewModel.Lines[1];
        Assert.NotNull(primaryRow.LinkedGroupKey);
        Assert.Equal(primaryRow.LinkedGroupKey, memberRow.LinkedGroupKey);
        Assert.True(memberRow.CanUpdateLink);
        Assert.False(memberRow.ShowStaticStatusBadge);
        Assert.True(viewModel.UpdateLinkedProductCommand.CanExecute(memberRow));

        // Edición en memoria de una fila ajena al grupo: debe sobrevivir a la recarga.
        viewModel.Lines[2].EditQuantityText = "7";
        viewModel.Lines[2].MarginRetailOverride = 33m;

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(memberRow);

        Assert.NotNull(capturedDialog);
        Assert.Equal(new[] { 101, 102 }, requestedProductIds!);
        Assert.NotNull(capturedLink);
        Assert.Equal(1, capturedLinkLineId);
        Assert.Equal(101, capturedLink!.ProductId);
        Assert.Equal(new[] { 101, 102 }, capturedLink.ProductIds!);
        Assert.Equal(new[] { 2 }, capturedLink.ReplacedLineIds!);
        Assert.Null(capturedLink.InvoiceDescription);
        Assert.NotNull(capturedLink.MarginOverrides);
        Assert.Contains(new LinkedProductMarginOverrideDto(101, 40m, 20m), capturedLink.MarginOverrides!);
        Assert.Contains("Vínculo actualizado: 2 producto(s).", viewModel.StatusMessage);

        var reloadedPrimary = viewModel.Lines[0];
        Assert.Equal(40m, reloadedPrimary.MarginRetailOverride);
        Assert.Equal(20m, reloadedPrimary.MarginWholesaleOverride);
        Assert.Equal(productA.CostPriceUSD, reloadedPrimary.OldCostPriceUSD);
        Assert.Equal(7m, viewModel.Lines[2].EditQuantity);
        Assert.Equal(33m, viewModel.Lines[2].MarginRetailOverride);
    }

    [Fact]
    public void CanUpdateLink_And_ShowStaticStatusBadge_ReflectResolutionAndStatus()
    {
        // 8.175-S1: solo las filas resueltas con estado Update ofrecen el botón Actualizar;
        // el badge estático se conserva para Unchanged/Conflict y las filas sin resolver.
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(
            CreateLine(1, resolvedProductId: 9, status: "Update"),
            CreateLine(2, resolvedProductId: 9, status: "Unchanged"),
            CreateLine(3, resolvedProductId: 9, status: "Conflict"),
            CreateLine(4, resolvedProductId: null, status: "New")));

        var updateRow = viewModel.Lines[0];
        var unchangedRow = viewModel.Lines[1];
        var conflictRow = viewModel.Lines[2];
        var unresolvedRow = viewModel.Lines[3];

        Assert.True(updateRow.CanUpdateLink);
        Assert.False(updateRow.ShowStaticStatusBadge);

        Assert.False(unchangedRow.CanUpdateLink);
        Assert.True(unchangedRow.ShowStaticStatusBadge);

        Assert.False(conflictRow.CanUpdateLink);
        Assert.True(conflictRow.ShowStaticStatusBadge);

        Assert.False(unresolvedRow.CanUpdateLink);
        Assert.False(unresolvedRow.ShowStaticStatusBadge);
    }

    [Fact]
    public async Task UpdateLinkedProduct_CreateMode_CallsCreateProductWithReplacedIds()
    {
        // 8.175-S4: desde la vista pre-cargada el revisor puede cambiar a Modo B; la creación
        // re-resuelve el ancla y absorbe las filas 1:N reemplazadas.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var primaryLine = CreateLine(1, unitCost: 24m, resolvedProductId: 101, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            Name = "REFRESCO SURTIDO CAJA",
            MatchMethod = "SupplierCode"
        };
        var memberLine = CreateLine(2, unitCost: 24m, resolvedProductId: 102, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            Name = "REFRESCO NARANJA CAJA",
            Quantity = 0m,
            MatchMethod = "SupplierCode"
        };
        var staged = CreateInvoice(primaryLine, memberLine);
        var refreshedAfterCreate = CreateInvoice(
            primaryLine with { Name = "Harina Nueva 1kg", OldCostPriceUSD = 0m });

        var productA = new ProductDto
        {
            Id = 101,
            SKU = "7591000001011",
            Name = "Refresco Cola 2L",
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m
        };
        var productB = new ProductDto
        {
            Id = 102,
            SKU = "7591000001028",
            Name = "Refresco Naranja 2L",
            CostPriceUSD = 1.40m,
            ProfitMarginRetail = 25m,
            ProfitMarginWholesale = 12m
        };

        CreateInvoiceProductRequestDto? capturedCreate = null;
        var capturedCreateLineId = 0;
        service.Setup(client => client.GetCatalogProductsByIdsAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { productA, productB });
        service.Setup(client => client.CreateProductFromLineAsync(
                staged.Id,
                It.IsAny<int>(),
                It.IsAny<CreateInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, CreateInvoiceProductRequestDto, CancellationToken>((_, lineId, request, _) =>
            {
                capturedCreateLineId = lineId;
                capturedCreate = request;
            })
            .ReturnsAsync(refreshedAfterCreate);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(dialogViewModel =>
            {
                Assert.True(dialogViewModel.IsUpdateMode);
                dialogViewModel.ResolutionMode = InvoiceProductResolutionMode.CreateNew;
                dialogViewModel.Barcode = "7591234567890";
                dialogViewModel.Name = "Harina Nueva 1kg";
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(viewModel.Lines[1]);

        Assert.NotNull(capturedCreate);
        Assert.Equal(1, capturedCreateLineId);
        Assert.Equal("7591234567890", capturedCreate!.Barcode);
        Assert.Equal("Harina Nueva 1kg", capturedCreate.Name);
        Assert.Equal(new[] { 2 }, capturedCreate.ReplacedLineIds!);
        Assert.Contains("Harina Nueva 1kg", viewModel.StatusMessage);
        service.Verify(client => client.LinkProductToLineAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<LinkInvoiceProductRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLinkedProduct_WhenCatalogFetchIncomplete_DoesNotOpenDialog()
    {
        // 8.175-S6: si un producto del vínculo ya no resuelve en el catálogo, el comando aborta
        // con error visible y no abre el modal (no se guarda una re-resolución que perdería el producto).
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var primaryLine = CreateLine(1, unitCost: 24m, resolvedProductId: 101, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            MatchMethod = "SupplierCode"
        };
        var memberLine = CreateLine(2, unitCost: 24m, resolvedProductId: 102, status: "Update") with
        {
            SupplierCode = "PROV-CAJA",
            Quantity = 0m,
            MatchMethod = "SupplierCode"
        };
        var staged = CreateInvoice(primaryLine, memberLine);
        var onlyProductA = new ProductDto { Id = 101, SKU = "7591000001011", Name = "Refresco Cola 2L" };

        service.Setup(client => client.GetCatalogProductsByIdsAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { onlyProductA });

        var dialog = new Mock<IDialogService>();
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(viewModel.Lines[1]);

        dialog.Verify(
            d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()),
            Times.Never);
        dialog.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        Assert.Contains(
            "No se pudieron cargar todos los productos vinculados de la línea",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task UpdateLinkedProduct_WhenDialogCancelled_MakesNoServiceCall()
    {
        // 8.175-S2: cancelar el modal pre-cargado no dispara ninguna re-resolución.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var staged = CreateInvoice(CreateLine(1, resolvedProductId: 101, status: "Update"));
        var productA = new ProductDto { Id = 101, SKU = "7591000001011", Name = "Refresco Cola 2L" };

        service.Setup(client => client.GetCatalogProductsByIdsAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { productA });

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Returns(false);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        service.Verify(client => client.LinkProductToLineAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<LinkInvoiceProductRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(client => client.CreateProductFromLineAsync(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<CreateInvoiceProductRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(viewModel.Lines);
    }

    [Fact]
    public async Task UpdateLinkedProduct_SingleResolvedLine_ReLinksWithoutReplacedIds()
    {
        // 8.175-S4/S7: una fila 1:1 puede re-vincularse a otro producto existente sin filas
        // reemplazadas; la grilla refresca el producto nuevo y su comparativa de costo.
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var stagedLine = CreateLine(1, unitCost: 24m, resolvedProductId: 101, status: "Update") with
        {
            SupplierCode = "PROV-BEBIDA",
            Name = "BEBIDA SURTIDA",
            MatchMethod = "SupplierCode",
            MarginRetailOverride = 30m,
            MarginWholesaleOverride = 15m
        };
        var staged = CreateInvoice(stagedLine);
        var productA = new ProductDto
        {
            Id = 101,
            SKU = "7591000001011",
            Name = "Bebida Uva 1.5L",
            CostPriceUSD = 8.00m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m
        };
        var productB = new ProductDto
        {
            Id = 102,
            SKU = "7591000001028",
            Name = "Bebida Piña 1.5L",
            CostPriceUSD = 7.80m,
            ProfitMarginRetail = 28m,
            ProfitMarginWholesale = 12m
        };
        var refreshedAfterReLink = CreateInvoice(stagedLine with
        {
            ResolvedProductId = 102,
            Name = productB.Name,
            OldCostPriceUSD = productB.CostPriceUSD,
            OldProfitMarginRetail = productB.ProfitMarginRetail,
            OldProfitMarginWholesale = productB.ProfitMarginWholesale,
            MarginRetailOverride = 28m,
            MarginWholesaleOverride = 12m
        });

        LinkInvoiceProductRequestDto? capturedLink = null;
        service.Setup(client => client.GetCatalogProductsByIdsAsync(
                It.IsAny<IReadOnlyList<int>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { productA });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                It.IsAny<int>(),
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, int, LinkInvoiceProductRequestDto, CancellationToken>((_, _, request, _) => capturedLink = request)
            .ReturnsAsync(refreshedAfterReLink);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(dialogViewModel =>
            {
                Assert.Equal(new[] { 101 }, dialogViewModel.SelectedProducts.Select(product => product.Id).ToArray());
                dialogViewModel.RemoveSelectedProductCommand.Execute(productA);
                dialogViewModel.ToggleProductSelectionCommand.Execute(productB);
            })
            .Returns(true);

        var viewModel = CreateViewModel(service.Object, dialog.Object);
        viewModel.LoadStagedInvoice(staged);

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(Assert.Single(viewModel.Lines));

        Assert.NotNull(capturedLink);
        Assert.Equal(102, capturedLink!.ProductId);
        Assert.Null(capturedLink.ProductIds);
        Assert.Null(capturedLink.ReplacedLineIds);
        Assert.Null(capturedLink.InvoiceDescription);
        Assert.Equal("Vínculo actualizado: 1 producto(s).", viewModel.StatusMessage);

        var reloadedLine = Assert.Single(viewModel.Lines);
        Assert.Equal(102, reloadedLine.ResolvedProductId);
        Assert.Equal(productB.CostPriceUSD, reloadedLine.OldCostPriceUSD);
        Assert.Equal(28m, reloadedLine.MarginRetailOverride);
    }

    [Fact]
    public async Task OcrScannerConfigDialogViewModel_BuildsTemplateRunsLiveTestAndSavesTemplate()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var filePicker = Mock.Of<IFilePickerDialog>();

        SupplierOcrTemplateDto? capturedTestTemplate = null;
        service.Setup(s => s.ExtractOcrAsync(
                "C:\\temp\\test.png",
                7,
                null,
                It.IsAny<SupplierOcrTemplateDto?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int?, SupplierColumnMappingDto?, SupplierOcrTemplateDto?, CancellationToken>(
                (_, _, _, tpl, _) => capturedTestTemplate = tpl)
            .ReturnsAsync(new OcrExtractionResultDto(
                new[]
                {
                    new OcrExtractedLineDto("COD-1", null, "HARINA PAN 1X40", 2m, 40m, 95m, 90m, 88m, ExtractedConversionFactor: 40m)
                },
                "J-123",
                "Proveedor OCR",
                "00099",
                RawExtractedText: "COD-1 HARINA PAN 1X40 2 40.00",
                PreprocessedPreviewPng: new byte[] { 1, 2, 3 }));

        SupplierOcrTemplateDto? savedTemplate = null;
        service.Setup(s => s.SaveSupplierOcrTemplateAsync(
                7,
                It.IsAny<SupplierOcrTemplateDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, SupplierOcrTemplateDto, CancellationToken>((_, tpl, _) => savedTemplate = tpl)
            .ReturnsAsync((int _, SupplierOcrTemplateDto tpl, CancellationToken _) => tpl);

        var vm = new OcrScannerConfigDialogViewModel(
            7,
            "Proveedor OCR",
            null,
            null,
            service.Object,
            filePicker,
            "C:\\temp\\test.png");

        vm.RoiX = 0.05;
        vm.RoiY = 0.10;
        vm.RoiWidth = 0.85;
        vm.RoiHeight = 0.70;
        vm.RotateRightCommand.Execute(null);
        vm.ContrastFactor = 1.3;
        vm.BrightnessDelta = -15;
        vm.UseCustomBinarizationThreshold = true;
        vm.BinarizationThreshold = 145;
        vm.HeaderAnchorKeywords = "RENGLON, DETALLE";
        vm.FooterAnchorKeywords = "SUBTOTAL, IVA";
        vm.SupplierCodeColumnPosition = 1;
        vm.QuantityColumnPosition = 2;
        vm.ProductColumnPosition = 3;
        vm.UnitCostColumnPosition = 4;
        vm.IsCostRuleLineTotal = true;
        vm.IsDecimalComma = true;
        vm.SelectedFactorPatternKey = "AUTO";
        vm.NewReplacementFrom = "O";
        vm.NewReplacementTo = "0";
        vm.AddReplacementRuleCommand.Execute(null);

        await vm.TestExtractionCommand.ExecuteAsync(null);

        Assert.NotNull(capturedTestTemplate);
        Assert.Equal(90, capturedTestTemplate!.RotationDegrees);
        Assert.Equal(145, capturedTestTemplate.BinarizationThreshold);
        Assert.Equal(OcrCostAmountRule.LineTotal, capturedTestTemplate.CostAmountRule);
        Assert.Equal(OcrDecimalSeparatorRule.Comma, capturedTestTemplate.DecimalSeparator);
        Assert.Equal("AUTO", capturedTestTemplate.FactorExtractionPattern);
        Assert.Single(vm.SandboxLines);
        Assert.Equal(40m, vm.SandboxLines[0].ExtractedConversionFactor);
        Assert.Contains("HARINA PAN 1X40", vm.RawExtractedText);
        Assert.Equal(new byte[] { 1, 2, 3 }, vm.PreprocessedPreviewBytes);

        await vm.ApplyToInvoiceCommand.ExecuteAsync(null);

        Assert.True(vm.ApplyResultToStaging);
        Assert.NotNull(savedTemplate);
        Assert.Equal(OcrCostAmountRule.LineTotal, savedTemplate!.CostAmountRule);
    }

    [Fact]
    public async Task ClientSupplierInvoiceService_GetAndSaveOcrTemplateRoutesAndMultipartOcrTemplateJson()
    {
        var template = new SupplierOcrTemplateDto(
            RoiX: 0.1,
            CostAmountRule: OcrCostAmountRule.LineTotal,
            FactorExtractionPattern: "AUTO");
        var handler = new StubHttpMessageHandler(
            JsonResponse(template),
            JsonResponse(template));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
        var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

        var loaded = await service.GetSupplierOcrTemplateAsync(7);
        var saved = await service.SaveSupplierOcrTemplateAsync(7, template);

        Assert.NotNull(loaded);
        Assert.Equal(OcrCostAmountRule.LineTotal, loaded!.CostAmountRule);
        Assert.Equal("AUTO", saved.FactorExtractionPattern);
        Assert.Equal(
            new[]
            {
                "/api/supplier-invoices/suppliers/7/ocr-template",
                "/api/supplier-invoices/suppliers/7/ocr-template"
            },
            handler.RequestPaths);
    }


    private static void PrepareStageableInvoice(SupplierInvoiceViewModel viewModel)
    {
        viewModel.SetFileHeaders(new[] { "Barcode", "Supplier Code", "Name", "Quantity", "Unit Cost" });
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Supplier", null);
        viewModel.SelectedFilePath = InvoiceFilePath;
    }

    private static async Task<StageSupplierInvoiceRequestDto> CaptureStageRequestAsync(
        string currency,
        string? appliedRateText)
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        StageSupplierInvoiceRequestDto? captured = null;
        service.Setup(client => client.ParseFileWithMappingAsync(
                InvoiceFilePath,
                It.IsAny<SupplierColumnMappingDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StageLineDto> { new("SUP-1", "12345", "Coffee", 2m, 365m) });
        service.Setup(client => client.StageAsync(
                It.IsAny<StageSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<StageSupplierInvoiceRequestDto, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(CreateInvoice(CreateLine(1)));
        var viewModel = CreateViewModel(service.Object);
        PrepareStageableInvoice(viewModel);
        viewModel.SelectedCurrency = currency;
        if (appliedRateText is not null)
        {
            viewModel.AppliedRateText = appliedRateText;
        }

        await viewModel.StageInvoiceCommand.ExecuteAsync(null);

        return Assert.IsType<StageSupplierInvoiceRequestDto>(captured);
    }

    private static Desktop.Client.Services.SupplierInvoiceService CreateClientService() => new(new HttpClient());

    private static Inventory.Module.Services.SupplierInvoiceService CreateBackendService(InventoryDbContext context) =>
        new(
            context,
            Mock.Of<ISystemSettingsService>(),
            Mock.Of<ISupplierProductSimilaritySearch>(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductManagementService>());

    private static SupplierInvoiceDetailDto CreateInvoice(params SupplierInvoiceLineDto[] lines) =>
        CreateInvoice(CurrencyCodes.Usd, lines);

    private static SupplierInvoiceDetailDto CreateInvoice(string currency, params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", currency, currency == CurrencyCodes.BsS ? 36.5m : 1m, lines);

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

    private static SupplierColumnMappingDto Mapping() =>
        new("Barcode", "Supplier Code", "Name", "Quantity", "Unit Cost");

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value)
    };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }

    /// <summary>Captura el cuerpo multipart (campos y bytes del archivo) para asertos de subida OCR.</summary>
    private sealed class MultipartCaptureHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private static readonly byte[] HeaderSeparator = "\r\n\r\n"u8.ToArray();
        private static readonly byte[] PartSeparator = "\r\n--"u8.ToArray();

        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];

        public byte[]? LastMultipartBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            if (request.Content is not null)
            {
                LastMultipartBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            return _responses.Dequeue();
        }

        public bool HasPart(string name) => FindPartHeader(name) >= 0;

        public string? GetFieldValue(string name)
        {
            var bytes = GetPartBytes(name);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        public string? GetFileName(string name)
        {
            var bytes = LastMultipartBytes;
            var headerIndex = FindPartHeader(name);
            if (bytes is null || headerIndex < 0)
            {
                return null;
            }

            var separatorIndex = IndexOf(bytes, HeaderSeparator, headerIndex);
            var headerEnd = separatorIndex < 0 ? bytes.Length : separatorIndex;

            var quotedMarker = "filename=\""u8.ToArray();
            var markerIndex = IndexOf(bytes, quotedMarker, headerIndex);
            if (markerIndex >= 0 && markerIndex < headerEnd)
            {
                var quotedStart = markerIndex + quotedMarker.Length;
                var quotedEnd = IndexOf(bytes, "\""u8.ToArray(), quotedStart);
                if (quotedEnd > quotedStart && quotedEnd <= headerEnd)
                {
                    return Encoding.UTF8.GetString(bytes, quotedStart, quotedEnd - quotedStart);
                }
            }

            var unquotedMarker = "filename="u8.ToArray();
            markerIndex = IndexOf(bytes, unquotedMarker, headerIndex);
            if (markerIndex < 0 || markerIndex >= headerEnd)
            {
                return null;
            }

            var valueStart = markerIndex + unquotedMarker.Length;
            var valueEnd = valueStart;
            while (valueEnd < headerEnd && bytes[valueEnd] is not (byte)';' and not (byte)'\r')
            {
                valueEnd++;
            }

            return Encoding.UTF8.GetString(bytes, valueStart, valueEnd - valueStart);
        }

        public byte[]? GetPartBytes(string name)
        {
            var bytes = LastMultipartBytes;
            var headerIndex = FindPartHeader(name);
            if (bytes is null || headerIndex < 0)
            {
                return null;
            }

            var separatorIndex = IndexOf(bytes, HeaderSeparator, headerIndex);
            if (separatorIndex < 0)
            {
                return null;
            }

            var start = separatorIndex + HeaderSeparator.Length;
            var end = IndexOf(bytes, PartSeparator, start);
            return end < 0 ? null : bytes[start..end];
        }

        private int FindPartHeader(string name)
        {
            var bytes = LastMultipartBytes;
            if (bytes is null)
            {
                return -1;
            }

            var quoted = IndexOf(bytes, Encoding.ASCII.GetBytes($"name=\"{name}\""), 0);
            if (quoted >= 0)
            {
                return quoted;
            }

            // MultipartFormDataContent omite las comillas en nombres ASCII simples (name=file; ...).
            var unquoted = Encoding.ASCII.GetBytes($"name={name}");
            var start = 0;
            while (true)
            {
                var index = IndexOf(bytes, unquoted, start);
                if (index < 0)
                {
                    return -1;
                }

                var after = index + unquoted.Length;
                if (after < bytes.Length && bytes[after] is (byte)';' or (byte)'\r' or (byte)'"')
                {
                    return index;
                }

                start = after;
            }
        }

        private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
        {
            if (pattern.Length == 0 || startIndex < 0 || startIndex > source.Length)
            {
                return -1;
            }

            var index = source.AsSpan(startIndex).IndexOf(pattern);
            return index < 0 ? -1 : startIndex + index;
        }
    }
}
