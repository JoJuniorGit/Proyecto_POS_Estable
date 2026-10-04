using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Json;
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
/// 8.147-S4/S5/S6 (T7): edición de campos OCR con bandas de confianza, correcciones al confirm,
/// etiqueta de página del panel de previews y gating de comandos sin UI (headless).
/// </summary>
public sealed class SupplierInvoiceOcrReviewTests
{
    [Theory]
    [InlineData(0, "Red")]
    [InlineData(59.99, "Red")]
    [InlineData(60, "Yellow")]
    [InlineData(84.99, "Yellow")]
    [InlineData(85, "None")]
    [InlineData(100, "None")]
    public void NameBand_MapsOcrConfidenceThresholds(decimal confidence, string expected)
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(ocrNameConfidence: confidence));

        Assert.Equal(expected, line.NameConfidenceBand);
    }

    [Fact]
    public void NameBand_NullConfidence_IsNone()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine());

        Assert.Equal("None", line.NameConfidenceBand);
    }

    [Fact]
    public void EachFieldBand_ReadsItsOwnConfidenceField()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(
            ocrNameConfidence: 90m,
            ocrQuantityConfidence: 50m,
            ocrUnitCostConfidence: 70m));

        Assert.Equal("None", line.NameConfidenceBand);
        Assert.Equal("Red", line.QuantityConfidenceBand);
        Assert.Equal("Yellow", line.UnitCostConfidenceBand);
    }

    [Fact]
    public void EditProperties_InitializeFromSource()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(name: "Arroz", quantity: 3m, unitCostDocument: 7.5m));

        Assert.False(line.IsEditorEnabled);
        Assert.Equal("Arroz", line.EditName);
        Assert.Equal(3m, line.EditQuantity);
        Assert.Equal(7.5m, line.EditUnitCostDocument);
    }

    [Fact]
    public void EditingField_ClearsOnlyItsBandAndRevertingRestoresIt()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(
            ocrNameConfidence: 50m,
            ocrQuantityConfidence: 50m,
            ocrUnitCostConfidence: 50m));
        line.IsEditorEnabled = true;

        line.EditName = "Cafe corregido";

        Assert.Equal("None", line.NameConfidenceBand);
        Assert.Equal("Red", line.QuantityConfidenceBand);
        Assert.Equal("Red", line.UnitCostConfidenceBand);

        line.EditName = "Coffee";
        Assert.Equal("Red", line.NameConfidenceBand);

        line.EditQuantity = 5m;
        Assert.Equal("None", line.QuantityConfidenceBand);
        Assert.Equal("Red", line.UnitCostConfidenceBand);

        line.EditUnitCostDocument = 12m;
        Assert.Equal("None", line.UnitCostConfidenceBand);
    }

    [Fact]
    public void EditingField_RaisesPropertyChangedForItsBand()
    {
        var line = new SupplierInvoiceLineViewModel(CreateLine(ocrNameConfidence: 50m));
        var changed = new List<string?>();
        line.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        line.EditName = "Otro nombre";

        Assert.Contains(nameof(SupplierInvoiceLineViewModel.NameConfidenceBand), changed);
    }

    [Fact]
    public async Task Confirm_OcrLine_SendsCorrectionsOnlyForChangedFields()
    {
        var (viewModel, service, dialog) = CreateConfirmViewModel();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CreateLine(ocrNameConfidence: 90m));
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged);
        viewModel.IsOcrSource = true;
        viewModel.LoadStagedInvoice(staged);
        var line = Assert.Single(viewModel.Lines);

        line.EditName = "Cafe corregido";
        line.EditQuantity = 4m;
        line.EditUnitCostDocument = 12.5m;

        await viewModel.ConfirmInvoiceAsync();

        var sent = Assert.Single(Assert.IsType<ConfirmSupplierInvoiceRequestDto>(captured).Lines);
        Assert.Equal("Cafe corregido", sent.Name);
        Assert.Equal(4m, sent.Quantity);
        Assert.Equal(12.5m, sent.UnitCostDocument);
        Assert.Equal(line.MarginRetailOverride, sent.MarginRetailOverride);
    }

    [Fact]
    public async Task Confirm_OcrLine_WithoutEdits_SendsNullCorrections()
    {
        var (viewModel, service, dialog) = CreateConfirmViewModel();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CreateLine(ocrNameConfidence: 90m));
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged);
        viewModel.IsOcrSource = true;
        viewModel.LoadStagedInvoice(staged);

        await viewModel.ConfirmInvoiceAsync();

        var sent = Assert.Single(Assert.IsType<ConfirmSupplierInvoiceRequestDto>(captured).Lines);
        Assert.Null(sent.Name);
        Assert.Null(sent.Quantity);
        Assert.Null(sent.UnitCostDocument);
    }

    [Fact]
    public async Task Confirm_OcrLine_EditedBackToSourceValue_SendsNullCorrection()
    {
        var (viewModel, service, dialog) = CreateConfirmViewModel();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CreateLine(ocrNameConfidence: 90m));
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged);
        viewModel.IsOcrSource = true;
        viewModel.LoadStagedInvoice(staged);
        var line = Assert.Single(viewModel.Lines);

        line.EditName = "Coffee";
        line.EditQuantity = 2m;
        line.EditUnitCostDocument = 10m;

        await viewModel.ConfirmInvoiceAsync();

        var sent = Assert.Single(Assert.IsType<ConfirmSupplierInvoiceRequestDto>(captured).Lines);
        Assert.Null(sent.Name);
        Assert.Null(sent.Quantity);
        Assert.Null(sent.UnitCostDocument);
    }

    [Fact]
    public async Task Confirm_TabularLine_DoesNotSendCorrectionsEvenIfEditPropertiesChange()
    {
        var (viewModel, service, dialog) = CreateConfirmViewModel();
        dialog.Setup(service => service.ShowConfirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        ConfirmSupplierInvoiceRequestDto? captured = null;
        var staged = CreateInvoice(CreateLine());
        service.Setup(client => client.ConfirmAsync(
                staged.Id,
                It.IsAny<ConfirmSupplierInvoiceRequestDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, ConfirmSupplierInvoiceRequestDto, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(staged);
        viewModel.LoadStagedInvoice(staged);
        var line = Assert.Single(viewModel.Lines);
        line.EditName = "Mutacion tabular";
        line.EditQuantity = 9m;
        line.EditUnitCostDocument = 99m;

        await viewModel.ConfirmInvoiceAsync();

        var sent = Assert.Single(Assert.IsType<ConfirmSupplierInvoiceRequestDto>(captured).Lines);
        Assert.Null(sent.Name);
        Assert.Null(sent.Quantity);
        Assert.Null(sent.UnitCostDocument);
    }

    [Fact]
    public void LoadStagedInvoice_SetsEditorEnabledOnlyForOcrOrigin()
    {
        var tabular = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        tabular.LoadStagedInvoice(CreateInvoice(CreateLine()));

        var ocr = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        ocr.IsOcrSource = true;
        ocr.LoadStagedInvoice(CreateInvoice(CreateLine()));

        Assert.False(Assert.Single(tabular.Lines).IsEditorEnabled);
        Assert.True(Assert.Single(ocr.Lines).IsEditorEnabled);
    }

    [Fact]
    public void ClearingOcrOrigin_DisablesExistingLineEditors()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        viewModel.IsOcrSource = true;
        viewModel.LoadStagedInvoice(CreateInvoice(CreateLine()));
        Assert.True(Assert.Single(viewModel.Lines).IsEditorEnabled);

        viewModel.IsOcrSource = false;

        Assert.False(Assert.Single(viewModel.Lines).IsEditorEnabled);
    }

    [Fact]
    public void OcrPageLabel_AndSelectedPreview_TrackPagesAndIndex()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());

        Assert.Equal(string.Empty, viewModel.OcrPageLabel);
        Assert.Null(viewModel.OcrSelectedPreviewBase64);

        viewModel.OcrPreviewPagesBase64 = new[] { "p1", "p2", "p3" };

        Assert.Equal("Página 1/3", viewModel.OcrPageLabel);
        Assert.Equal("p1", viewModel.OcrSelectedPreviewBase64);

        viewModel.OcrNextPageCommand.Execute(null);

        Assert.Equal("Página 2/3", viewModel.OcrPageLabel);
        Assert.Equal("p2", viewModel.OcrSelectedPreviewBase64);

        viewModel.OcrPreviewPagesBase64 = new[] { "solo" };

        Assert.Equal("Página 1/1", viewModel.OcrPageLabel);
        Assert.Equal("solo", viewModel.OcrSelectedPreviewBase64);
    }

    [Fact]
    public void OcrPageLabel_RaisesPropertyChangedOnIndexAndPageCountChanges()
    {
        var viewModel = CreateViewModel(Mock.Of<ClientSupplierInvoiceService>());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        viewModel.OcrPreviewPagesBase64 = new[] { "p1", "p2" };
        viewModel.OcrNextPageCommand.Execute(null);

        Assert.Contains(nameof(SupplierInvoiceViewModel.OcrPageLabel), changed);
        Assert.Contains(nameof(SupplierInvoiceViewModel.OcrSelectedPreviewBase64), changed);
    }

    [Fact]
    public void OcrFileFilter_AdvertisesAdditionalImageFormats()
    {
        Assert.Equal(
            "Facturas escaneadas (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.pdf)|" +
            "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff;*.pdf|" +
            "Imágenes (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff)|" +
            "*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tif;*.tiff|PDF (*.pdf)|*.pdf",
            SupplierInvoiceViewModel.OcrFileFilter);
    }

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".bmp", "image/bmp")]
    [InlineData(".tif", "image/tiff")]
    [InlineData(".tiff", "image/tiff")]
    [InlineData(".pdf", "application/pdf")]
    public async Task ExtractOcrAsync_UploadsFileWithExpectedContentType(string extension, string expectedMediaType)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ocr-upload-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
        try
        {
            var handler = new ContentTypeCaptureHandler();
            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") };
            var service = new Desktop.Client.Services.SupplierInvoiceService(httpClient);

            await service.ExtractOcrAsync(filePath, null, null);

            Assert.Equal(expectedMediaType, handler.FileContentType);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private static (SupplierInvoiceViewModel ViewModel, Mock<ClientSupplierInvoiceService> Service, Mock<IDialogService> Dialog)
        CreateConfirmViewModel()
    {
        var service = new Mock<ClientSupplierInvoiceService>(MockBehavior.Strict);
        var dialog = new Mock<IDialogService>();
        var viewModel = CreateViewModel(service.Object, dialog.Object);
        return (viewModel, service, dialog);
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
            Mock.Of<IFilePickerDialog>(),
            Mock.Of<IExchangeRateService>());
    }

    private static SupplierInvoiceDetailDto CreateInvoice(params SupplierInvoiceLineDto[] lines) =>
        new(42, 7, "Draft", CurrencyCodes.Usd, 1m, lines);

    private static SupplierInvoiceLineDto CreateLine(
        decimal? ocrNameConfidence = null,
        decimal? ocrQuantityConfidence = null,
        decimal? ocrUnitCostConfidence = null,
        string? name = "Coffee",
        decimal quantity = 2m,
        decimal unitCostDocument = 10m,
        bool isApproved = true,
        int? resolvedProductId = 9) => new(
            1,
            "SUP-1",
            "12345",
            name,
            quantity,
            unitCostDocument,
            10m,
            "Update",
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
            ocrNameConfidence,
            ocrQuantityConfidence,
            ocrUnitCostConfidence);

    /// <summary>Captura el Content-Type de la parte "file" del multipart OCR sin salir a la red.</summary>
    private sealed class ContentTypeCaptureHandler : HttpMessageHandler
    {
        public string? FileContentType { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            FileContentType = multipart.First().Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new OcrExtractionResultDto([], [], null, null))
            });
        }
    }
}
