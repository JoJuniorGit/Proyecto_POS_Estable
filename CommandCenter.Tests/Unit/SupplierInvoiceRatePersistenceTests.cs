using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// 8.178: aislamiento de la cabecera (Tasa aplicada / Moneda) frente a la acción "Actualizar"
/// (modal "Resolver producto desde factura"). La corrección manual de la tasa jamás debe revertirse
/// al valor detectado por el OCR ni re-asignarse al recargar los datos de la fila; la grilla y los
/// precios proyectados del modal deben usar la tasa viva de la UI.
/// </summary>
public sealed class SupplierInvoiceRatePersistenceTests
{
    private const string OcrFilePath = "C:\\facturas\\factura-escaneada.png";
    private const decimal OcrDetectedRate = 78.45m; // tasa ERRÓNEA detectada por el OCR
    private const string CorrectedRateText = "36,50"; // corrección manual del revisor

    [Fact]
    public async Task UpdateLinkedProduct_WithoutFlip_InjectsLiveRateIntoReloadedRowsAndSnapshot()
    {
        // Defecto reportado (criterio 2): al guardar el modal de vínculo, la recarga debe inyectar
        // la tasa VIVA del TextBox en las filas recargadas y en el snapshot local, no el valor
        // histórico guardado por el escaneo.
        var (viewModel, fixture) = await CreateScannedWithFixtureAsync();

        Assert.Equal("78.45", viewModel.AppliedRateText);
        viewModel.AppliedRateText = CorrectedRateText;
        Assert.Equal(21.49m, viewModel.Lines[0].UnitCostUSD); // 784,50 / 36,50

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.NotNull(fixture.CapturedDialog);
        Assert.Equal(21.49m, fixture.CapturedDialog!.LineUnitCostUSD); // criterio 3: modal con tasa viva
        Assert.Equal(CorrectedRateText, viewModel.AppliedRateText);
        Assert.Equal(CurrencyCodes.BsS, viewModel.SelectedCurrency);
        Assert.Equal(21.49m, viewModel.Lines[0].UnitCostUSD);
        Assert.Equal(36.5m, viewModel.StagedInvoice!.AppliedRate); // no el 78.45 del escaneo
    }

    [Fact]
    public async Task CurrencyFlipBack_AfterManualRateCorrection_KeepsCorrectedRate_NotOcrOriginal()
    {
        // Defecto reportado (criterios 1/2): volver a Bs.S no puede reponer la tasa OCR original
        // sobre la corrección manual del revisor.
        var (viewModel, _) = await CreateScannedWithFixtureAsync();

        viewModel.AppliedRateText = CorrectedRateText;
        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        viewModel.SelectedCurrency = CurrencyCodes.BsS;

        Assert.Equal(CorrectedRateText, viewModel.AppliedRateText);
        Assert.Equal(21.49m, viewModel.Lines[0].UnitCostUSD);
    }

    [Fact]
    public async Task UpdateLinkedProduct_AfterCurrencyFlipAndRateCorrection_DoesNotReassignHeader()
    {
        // Defecto reportado (criterio 1): la acción Actualizar se limita a la fila; no debe
        // re-asignar Moneda ni Tasa de la cabecera al recargar la factura.
        var (viewModel, fixture) = await CreateScannedWithFixtureAsync();

        viewModel.AppliedRateText = CorrectedRateText;
        viewModel.SelectedCurrency = CurrencyCodes.Usd;
        var expectedUsdCost = viewModel.Lines[0].UnitCostUSD; // 784,50 interpretado en USD

        await viewModel.UpdateLinkedProductCommand.ExecuteAsync(viewModel.Lines[0]);

        Assert.NotNull(fixture.CapturedDialog);
        Assert.Equal(expectedUsdCost, fixture.CapturedDialog!.LineUnitCostUSD); // criterio 3
        Assert.Equal(CurrencyCodes.Usd, viewModel.SelectedCurrency); // sin re-alineación a Bs.S
        Assert.Equal(CorrectedRateText, viewModel.AppliedRateText); // sin revert a la tasa OCR
        Assert.Equal(CurrencyCodes.Usd, viewModel.StagedInvoice!.Currency); // snapshot con estado vivo
        Assert.Equal(1m, viewModel.StagedInvoice.AppliedRate);
        Assert.Equal(expectedUsdCost, viewModel.Lines[0].UnitCostUSD);
    }

    // ---- helpers ----

    private sealed class ScanFixture
    {
        public CreateInvoiceProductDialogViewModel? CapturedDialog { get; set; }

        public ProductDto ProductA { get; } = new()
        {
            Id = 101,
            SKU = "7591000001011",
            Name = "Refresco Cola 2L",
            CostPriceUSD = 1.50m,
            ProfitMarginRetail = 30m,
            ProfitMarginWholesale = 15m
        };
    }

    private static async Task<(SupplierInvoiceViewModel ViewModel, ScanFixture Fixture)> CreateScannedWithFixtureAsync()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var fixture = new ScanFixture();

        var extracted = new OcrExtractionResultDto(
            new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 784.50m, 92m, 90m, 88m) },
            "J-123",
            "Proveedor OCR",
            "0238317",
            OcrDetectedRate);
        var staged = new SupplierInvoiceDetailDto(
            42,
            7,
            "Draft",
            CurrencyCodes.BsS,
            OcrDetectedRate,
            new[] { CreateLine(1, unitCost: 10m, unitCostDocument: 784.50m) });

        service.Setup(client => client.ExtractOcrAsync(OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(extracted);
        service.Setup(client => client.StageAsync(It.IsAny<StageSupplierInvoiceRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staged);
        service.Setup(client => client.GetCatalogProductsByIdsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { fixture.ProductA });
        service.Setup(client => client.LinkProductToLineAsync(
                staged.Id,
                It.IsAny<int>(),
                It.IsAny<LinkInvoiceProductRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(staged);

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowCreateInvoiceProductDialog(It.IsAny<CreateInvoiceProductDialogViewModel>()))
            .Callback<CreateInvoiceProductDialogViewModel>(dialogViewModel => fixture.CapturedDialog = dialogViewModel)
            .Returns(true);

        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        var viewModel = new SupplierInvoiceViewModel(
            service.Object,
            userSession,
            dialog.Object,
            PickerReturning(OcrFilePath),
            CreateRateService(36.50m));
        viewModel.SelectedSupplier = new SupplierSummaryDto(7, "J-123", "Proveedor", null);

        await viewModel.ScanInvoiceCommand.ExecuteAsync(null);
        return (viewModel, fixture);
    }

    private static IFilePickerDialog PickerReturning(string filePath)
    {
        var picker = new Mock<IFilePickerDialog>();
        picker.Setup(dialog => dialog.PickFilePath(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(filePath);
        return picker.Object;
    }

    private static IExchangeRateService CreateRateService(decimal rate)
    {
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(service => service.GetCurrentRateAsync())
            .ReturnsAsync((rate, (DateTime?)null));
        return rateService.Object;
    }

    private static SupplierInvoiceLineDto CreateLine(
        int id,
        decimal unitCost = 10m,
        bool isApproved = true,
        int? resolvedProductId = 101,
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
