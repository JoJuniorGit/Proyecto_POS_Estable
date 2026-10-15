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

public sealed class SupplierInvoiceUnifiedIngestionTests
{
    private static SupplierInvoiceViewModel CreateViewModel(
        ClientSupplierInvoiceService? invoiceService = null,
        IDialogService? dialogService = null,
        IFilePickerDialog? filePicker = null,
        UserRole role = UserRole.Manager)
    {
        var userSession = new UserSession();
        userSession.SetUser(new UserDto { Id = 1, Name = "Test User", Role = role });

        var exchangeRateService = new Mock<IExchangeRateService>();
        exchangeRateService.Setup(s => s.GetCurrentRateAsync())
            .ReturnsAsync((60m, (DateTime?)null));

        return new SupplierInvoiceViewModel(
            invoiceService ?? Mock.Of<ClientSupplierInvoiceService>(),
            userSession,
            dialogService ?? Mock.Of<IDialogService>(),
            filePicker ?? Mock.Of<IFilePickerDialog>(),
            exchangeRateService.Object);
    }

    [Fact]
    public void UnifiedInvoiceFileFilter_ContainsAllExpectedFormats()
    {
        var filter = SupplierInvoiceViewModel.UnifiedInvoiceFileFilter;

        Assert.Contains("*.xlsx", filter);
        Assert.Contains("*.xls", filter);
        Assert.Contains("*.csv", filter);
        Assert.Contains("*.xml", filter);
        Assert.Contains("*.png", filter);
        Assert.Contains("*.jpg", filter);
        Assert.Contains("*.jpeg", filter);
        Assert.Contains("*.webp", filter);
        Assert.Contains("*.bmp", filter);
        Assert.Contains("*.tif", filter);
        Assert.Contains("*.tiff", filter);
        Assert.Contains("*.pdf", filter);
        Assert.Contains("*.*", filter);
    }

    [Fact]
    public async Task LoadInvoiceCommand_PicksFileUsingUnifiedFilter_AndDispatches()
    {
        var filePickerMock = new Mock<IFilePickerDialog>();
        var serviceMock = new Mock<ClientSupplierInvoiceService>();

        filePickerMock.Setup(p => p.PickFilePath("Cargar factura de proveedor", SupplierInvoiceViewModel.UnifiedInvoiceFileFilter))
            .Returns(@"C:\invoices\test.xlsx");

        serviceMock.Setup(s => s.ReadHeadersAsync(@"C:\invoices\test.xlsx", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Code", "Name", "Qty", "Price" });

        var viewModel = CreateViewModel(serviceMock.Object, filePicker: filePickerMock.Object);

        Assert.True(viewModel.CanLoadInvoice);
        await viewModel.LoadInvoiceCommand.ExecuteAsync(null);

        filePickerMock.Verify(p => p.PickFilePath("Cargar factura de proveedor", SupplierInvoiceViewModel.UnifiedInvoiceFileFilter), Times.Once);
        serviceMock.Verify(s => s.ReadHeadersAsync(@"C:\invoices\test.xlsx", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(@"C:\invoices\test.xlsx", viewModel.SelectedFilePath);
    }

    [Theory]
    [InlineData(@"C:\invoices\doc.xlsx")]
    [InlineData(@"C:\invoices\doc.xls")]
    [InlineData(@"C:\invoices\doc.csv")]
    [InlineData(@"C:\invoices\doc.xml")]
    public async Task ProcessFileAsync_RoutesTabularFiles_ToLoadFileAsync(string filePath)
    {
        var serviceMock = new Mock<ClientSupplierInvoiceService>();
        serviceMock.Setup(s => s.ReadHeadersAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Code", "Name", "Price" });

        var viewModel = CreateViewModel(serviceMock.Object);

        await viewModel.ProcessFileAsync(filePath);

        serviceMock.Verify(s => s.ReadHeadersAsync(filePath, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(filePath, viewModel.SelectedFilePath);
        Assert.False(viewModel.IsOcrSource);
        Assert.Equal("Excel / XML", viewModel.IngestionSourceBadge);
    }

    [Theory]
    [InlineData(@"C:\invoices\scan.png")]
    [InlineData(@"C:\invoices\scan.jpg")]
    [InlineData(@"C:\invoices\scan.jpeg")]
    [InlineData(@"C:\invoices\scan.webp")]
    [InlineData(@"C:\invoices\scan.bmp")]
    [InlineData(@"C:\invoices\scan.tif")]
    [InlineData(@"C:\invoices\scan.tiff")]
    [InlineData(@"C:\invoices\scan.pdf")]
    public async Task ProcessFileAsync_RoutesImageAndPdfFiles_ToScanOcrFileAsync_WhenSupplierSelected(string filePath)
    {
        var serviceMock = new Mock<ClientSupplierInvoiceService>();
        var supplier = new SupplierSummaryDto(10, "J-99999999", "Distribuidora Polar", null);

        serviceMock.Setup(s => s.ExtractOcrAsync(filePath, supplier.Id, It.IsAny<SupplierColumnMappingDto?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OcrExtractionResultDto(
                new List<OcrExtractedLineDto> { new("POL-1", "7591234567890", "Harina Pan 1kg", 10m, 1.25m, 95m, 95m, 95m) },
                "J-99999999",
                "Distribuidora Polar",
                "FAC-2026-001",
                null));

        var viewModel = CreateViewModel(serviceMock.Object);
        viewModel.SelectedSupplier = supplier;

        await viewModel.ProcessFileAsync(filePath);

        serviceMock.Verify(s => s.ExtractOcrAsync(filePath, supplier.Id, It.IsAny<SupplierColumnMappingDto?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(viewModel.IsOcrSource);
        Assert.Equal("Lectura OCR", viewModel.IngestionSourceBadge);
        Assert.Equal("FAC-2026-001", viewModel.DisplayInvoiceNumber);
    }

    [Fact]
    public async Task ProcessFileAsync_ShowsWarning_WhenOcrFileGivenWithoutSelectedSupplier()
    {
        var dialogMock = new Mock<IDialogService>();
        var serviceMock = new Mock<ClientSupplierInvoiceService>();

        var viewModel = CreateViewModel(serviceMock.Object, dialogService: dialogMock.Object);
        viewModel.SelectedSupplier = null;

        await viewModel.ProcessFileAsync(@"C:\invoices\photo.png");

        dialogMock.Verify(d => d.ShowMessage("Proveedor requerido", It.Is<string>(msg => msg.Contains("proveedor"))), Times.Once);
        serviceMock.Verify(s => s.ExtractOcrAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SupplierColumnMappingDto?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(viewModel.IsOcrSource);
    }

    [Fact]
    public async Task ProcessFileAsync_ShowsWarning_WhenUnsupportedFileExtension()
    {
        var dialogMock = new Mock<IDialogService>();
        var serviceMock = new Mock<ClientSupplierInvoiceService>();

        var viewModel = CreateViewModel(serviceMock.Object, dialogService: dialogMock.Object);

        await viewModel.ProcessFileAsync(@"C:\invoices\invoice.docx");

        dialogMock.Verify(d => d.ShowMessage("Formato no compatible", It.Is<string>(msg => msg.Contains(".docx"))), Times.Once);
        serviceMock.Verify(s => s.ReadHeadersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        serviceMock.Verify(s => s.ExtractOcrAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SupplierColumnMappingDto?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void IngestionSourceBadge_ReflectsSourceStateCorrectly()
    {
        var viewModel = CreateViewModel();

        // 1. Initial: neither tabular nor OCR
        Assert.Equal(string.Empty, viewModel.IngestionSourceBadge);

        // 2. Tabular file loaded
        viewModel.SelectedFilePath = @"C:\data\factura.xlsx";
        Assert.Equal("Excel / XML", viewModel.IngestionSourceBadge);

        // 3. OCR active
        viewModel.IsOcrSource = true;
        Assert.Equal("Lectura OCR", viewModel.IngestionSourceBadge);

        // 4. Reset
        viewModel.IsOcrSource = false;
        viewModel.SelectedFilePath = string.Empty;
        Assert.Equal(string.Empty, viewModel.IngestionSourceBadge);
    }

    [Fact]
    public void DisplayInvoiceNumber_FollowsSourceAndNotifiesOnChanges()
    {
        var viewModel = CreateViewModel();
        var changedProps = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
            {
                changedProps.Add(e.PropertyName);
            }
        };

        // 1. Tabular / Non-OCR mode default
        Assert.Equal("No asignado", viewModel.DisplayInvoiceNumber);

        // 2. Set InvoiceNumber
        viewModel.InvoiceNumber = "INV-2026-99";
        Assert.Equal("INV-2026-99", viewModel.DisplayInvoiceNumber);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DisplayInvoiceNumber), changedProps);

        // 3. Switch to OCR mode without detected invoice
        changedProps.Clear();
        viewModel.IsOcrSource = true;
        Assert.Equal("No detectado", viewModel.DisplayInvoiceNumber);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DisplayInvoiceNumber), changedProps);

        // 4. Set OcrDetectedInvoiceNumber
        changedProps.Clear();
        viewModel.OcrDetectedInvoiceNumber = "OCR-FACT-88";
        Assert.Equal("OCR-FACT-88", viewModel.DisplayInvoiceNumber);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DisplayInvoiceNumber), changedProps);

        // 5. Switch back to tabular mode
        changedProps.Clear();
        viewModel.IsOcrSource = false;
        Assert.Equal("INV-2026-99", viewModel.DisplayInvoiceNumber);
        Assert.Contains(nameof(SupplierInvoiceViewModel.DisplayInvoiceNumber), changedProps);
    }

    [Fact]
    public void CanLoadInvoice_FollowsRolePermissionsAndBusyState()
    {
        // Manager can load
        var managerVm = CreateViewModel(role: UserRole.Manager);
        Assert.True(managerVm.CanLoadInvoice);

        // Admin can load
        var adminVm = CreateViewModel(role: UserRole.Admin);
        Assert.True(adminVm.CanLoadInvoice);

        // Driver blocked
        var driverVm = CreateViewModel(role: UserRole.Driver);
        Assert.False(driverVm.CanLoadInvoice);

        // Cashier blocked (cannot mutate catalog)
        var cashierVm = CreateViewModel(role: UserRole.Cashier);
        Assert.False(cashierVm.CanLoadInvoice);

        // Manager when Busy
        managerVm.IsBusy = true;
        Assert.False(managerVm.CanLoadInvoice);
    }
}
