using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClosedXML.Excel;
using CommandCenter.Tests.Builders;
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
    [InlineData("New", "[NEW]")]
    [InlineData("Update", "[UPDATE]")]
    [InlineData("Unchanged", "[UNCHANGED]")]
    [InlineData("Conflict", "[CONFLICT]")]
    public void StatusBadge_MapsBackendStatus(string status, string expected)
    {
        Assert.Equal(expected, SupplierInvoiceLineViewModel.GetStatusLabel(status));
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
        Assert.Equal(4.25m, line.UnitCostUSD);
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
        Assert.Equal(4.25m, line.UnitCostUSD);
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
        Assert.Equal(5.50m, line.UnitCostUSD);
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
            new[] { new StageLineDto("SUP-1", "12345", "Coffee", 2m, 4m) }));
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

    private static SupplierInvoiceViewModel CreateViewModel(
        ClientSupplierInvoiceService invoiceService,
        IDialogService? dialogService = null)
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Manager", Role = UserRole.Manager });
        return new SupplierInvoiceViewModel(
            invoiceService,
            userSession,
            dialogService ?? Mock.Of<IDialogService>(),
            Mock.Of<IFilePickerDialog>());
    }

    private static Desktop.Client.Services.SupplierInvoiceService CreateClientService() => new(new HttpClient());

    private static Inventory.Module.Services.SupplierInvoiceService CreateBackendService(InventoryDbContext context) =>
        new(context, Mock.Of<ISystemSettingsService>(), Mock.Of<ISupplierProductSimilaritySearch>(), Mock.Of<ICurrentUserService>());

    private static SupplierInvoiceDetailDto CreateInvoice(params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", lines);

    private static SupplierInvoiceLineDto CreateLine(
        int id,
        decimal unitCost = 10m,
        bool isApproved = true) => new(
            id,
            "SUP-1",
            "12345",
            "Coffee",
            2m,
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
            isApproved,
            "Barcode");

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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
