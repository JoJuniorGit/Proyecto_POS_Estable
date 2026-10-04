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
    public void MarginEdit_RecalculatesSuggestedPriceWithoutCallingApi()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var viewModel = CreateViewModel(service.Object);
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1, unitCost: 10m)));
        var line = Assert.Single(viewModel.Lines);

        line.MarginRetailOverride = 33.333m;

        Assert.Equal(PricingCalculator.RoundPriceUp(10m * (1m + 33.333m / 100m)), line.SuggestedRetailPriceUSD);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public void MarginEdit_RaisesPropertyChangeForSuggestedRetailPrice()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine(1, unitCost: 10m)));
        var line = Assert.Single(viewModel.Lines);
        var changedProperties = new List<string?>();
        line.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        line.MarginRetailOverride = 40m;

        Assert.Contains(nameof(SupplierInvoiceLineViewModel.SuggestedRetailPriceUSD), changedProperties);
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
    [InlineData("Update", "[UPDATE]")]
    [InlineData("Unchanged", "[UNCHANGED]")]
    [InlineData("Conflict", "[CONFLICT]")]
    public void StatusBadge_MapsBackendStatus(string status, string expected)
    {
        Assert.Equal(expected, SupplierInvoiceLineViewModel.GetStatusLabel(status));
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
        Assert.Equal("[UPDATE]", line.StatusLabel);
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
    public async Task LookupSupplier_SingleMatchWithMissingSavedColumns_KeepsSpecificWarning()
    {
        var savedMapping = new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost");
        var supplier = new SupplierSummaryDto(7, "J-123", "Supplier", savedMapping);
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.GetSuppliersAsync("J-123", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { supplier });
        var viewModel = CreateViewModel(service.Object);
        viewModel.SetFileHeaders(new[] { "EAN", "Vendor Code", "Item", "Units" });
        viewModel.SupplierRifOrNit = "J-123";

        await viewModel.LookupSupplierCommand.ExecuteAsync(null);

        Assert.Contains("no aparecen en el archivo", viewModel.StatusMessage);
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
            "3;SUP-3;Ambos;1.234,56;1,234.56"));

        var lines = await service.ParseFileWithMappingAsync(stream, ".csv", Mapping());

        Assert.Equal(3, lines.Count);
        Assert.Equal(4250m, lines[0].Quantity);
        Assert.Equal(1234567m, lines[0].UnitCostDocument);
        Assert.Equal(4.25m, lines[1].Quantity);
        Assert.Equal(4.25m, lines[1].UnitCostDocument);
        Assert.Equal(1234.56m, lines[2].Quantity);
        Assert.Equal(1234.56m, lines[2].UnitCostDocument);
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
                new[] { "cGFnZTE=" },
                "J-123",
                "Proveedor OCR");
            var handler = new MultipartCaptureHandler(JsonResponse(extracted));
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
            var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

            var result = await service.ExtractOcrAsync(
                filePath,
                7,
                new SupplierColumnMappingDto("EAN", "Vendor Code", "Item", "Units", "Cost"));

            Assert.Equal("J-123", result.DetectedRif);
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
                JsonResponse(new OcrExtractionResultDto([], [], null, null)));
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
                JsonResponse(new OcrExtractionResultDto([], [], null, null)));
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
            new[] { "cGFnZTE=", "cGFnZTI=" },
            "J-123",
            "Proveedor OCR");
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
        Assert.Equal(new[] { "cGFnZTE=", "cGFnZTI=" }, viewModel.OcrPreviewPagesBase64);
        Assert.Equal(0, viewModel.OcrSelectedPageIndex);
        Assert.Equal(1.0, viewModel.OcrZoom);
        Assert.Equal("J-123", viewModel.OcrDetectedRif);
        Assert.Equal("Proveedor OCR", viewModel.OcrDetectedSupplierName);
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
                new[] { "cGFnZTE=" },
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
        Assert.Empty(viewModel.OcrPreviewPagesBase64);
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
                new[] { "cGFnZTE=" },
                "J-999",
                "Proveedor Detectado"));
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
                new[] { "cGFnZTE=" },
                "J-999",
                "Proveedor Detectado"));
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
    public void OcrZoom_ClampsToRangeAndCommandsStepAndReset()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());

        viewModel.OcrZoom = 0.1;
        Assert.Equal(0.5, viewModel.OcrZoom);

        viewModel.OcrZoom = 10.0;
        Assert.Equal(4.0, viewModel.OcrZoom);

        viewModel.OcrZoomOutCommand.Execute(null);
        Assert.Equal(3.75, viewModel.OcrZoom);

        viewModel.OcrZoomInCommand.Execute(null);
        Assert.Equal(4.0, viewModel.OcrZoom);

        viewModel.OcrZoomInCommand.Execute(null);
        Assert.Equal(4.0, viewModel.OcrZoom);

        viewModel.ResetOcrZoomCommand.Execute(null);
        Assert.Equal(1.0, viewModel.OcrZoom);

        viewModel.OcrZoom = 0.5;
        viewModel.OcrZoomOutCommand.Execute(null);
        Assert.Equal(0.5, viewModel.OcrZoom);
    }

    [Fact]
    public void OcrPageNavigation_ClampsBetweenPagesAndResetsOnSourceChange()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());

        Assert.False(viewModel.HasOcrPreviews);
        Assert.False(viewModel.CanNavigateOcrNextPage);
        Assert.False(viewModel.CanNavigateOcrPreviousPage);
        viewModel.OcrNextPageCommand.Execute(null);
        Assert.Equal(0, viewModel.OcrSelectedPageIndex);

        viewModel.OcrPreviewPagesBase64 = new[] { "a", "b", "c" };

        Assert.Equal(0, viewModel.OcrSelectedPageIndex);
        Assert.False(viewModel.CanNavigateOcrPreviousPage);
        Assert.True(viewModel.CanNavigateOcrNextPage);

        viewModel.OcrNextPageCommand.Execute(null);
        viewModel.OcrNextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.OcrSelectedPageIndex);
        Assert.False(viewModel.CanNavigateOcrNextPage);

        viewModel.OcrNextPageCommand.Execute(null);
        Assert.Equal(2, viewModel.OcrSelectedPageIndex);

        viewModel.OcrPreviousPageCommand.Execute(null);
        Assert.Equal(1, viewModel.OcrSelectedPageIndex);
        Assert.True(viewModel.CanNavigateOcrNextPage);

        viewModel.OcrSelectedPageIndex = 99;
        Assert.Equal(2, viewModel.OcrSelectedPageIndex);

        viewModel.OcrPreviewPagesBase64 = new[] { "a" };
        Assert.Equal(0, viewModel.OcrSelectedPageIndex);
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
                new[] { "cGFnZTE=", "cGFnZTI=" },
                null,
                null));
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

        await viewModel.ConfirmInvoiceAsync();

        Assert.False(viewModel.IsOcrSource);
        Assert.Empty(viewModel.OcrPreviewPagesBase64);
        Assert.Equal(0, viewModel.OcrSelectedPageIndex);
        Assert.Equal(1.0, viewModel.OcrZoom);
        Assert.Null(viewModel.OcrDetectedRif);
        Assert.Null(viewModel.OcrDetectedSupplierName);
    }

    [Fact]
    public async Task TabularFileSelection_ClearsOcrReviewState()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        service.Setup(client => client.ExtractOcrAsync(
                OcrFilePath, 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new[] { new OcrExtractedLineDto("SUP-1", "12345", "Coffee", 2m, 4.25m, 90m, 80m, 70m) },
                new[] { "cGFnZTE=", "cGFnZTI=" },
                "J-123",
                "Proveedor OCR"));
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
        viewModel.OcrSelectedPageIndex = 1;
        viewModel.OcrZoom = 3.0;

        await viewModel.LoadFileAsync(TabularFilePath);

        Assert.False(viewModel.IsOcrSource);
        Assert.Empty(viewModel.OcrPreviewPagesBase64);
        Assert.Equal(0, viewModel.OcrSelectedPageIndex);
        Assert.Equal(1.0, viewModel.OcrZoom);
        Assert.Null(viewModel.OcrDetectedRif);
        Assert.Null(viewModel.OcrDetectedSupplierName);
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
