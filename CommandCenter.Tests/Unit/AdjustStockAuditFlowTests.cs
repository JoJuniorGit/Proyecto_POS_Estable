using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Desktop.Client.Views;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.177-T3 (S1/S4): endurecimiento del ajuste manual — motivo estandarizado y observación
/// obligatorios en la UI, propagación del contrato auditado hasta el body JSON y bloqueo del
/// servicio cuando el diálogo no confirma.
/// </summary>
public sealed class AdjustStockAuditFlowTests
{
    [Fact]
    public void ValidateAdjustment_WithoutStandardReason_BlocksAndPointsToReasonCode()
    {
        var result = AdjustStockDialog.ValidateAdjustment("5", "Merma por empaque roto", null, "Caja aplastada en anaquel 3");

        Assert.False(result.IsValid);
        Assert.Equal("reasonCode", result.FocusField);
    }

    [Fact]
    public void ValidateAdjustment_WithoutObservation_BlocksAndPointsToObservation()
    {
        var result = AdjustStockDialog.ValidateAdjustment("5", "Merma por empaque roto", StockMovementReason.Damage, "   ");

        Assert.False(result.IsValid);
        Assert.Equal("observation", result.FocusField);
    }

    [Fact]
    public void ValidateAdjustment_WithCompleteData_IsValid()
    {
        var result = AdjustStockDialog.ValidateAdjustment("5", "Merma por empaque roto", StockMovementReason.Damage, "Caja aplastada en anaquel 3");

        Assert.True(result.IsValid);
        Assert.Equal(string.Empty, result.FocusField);
    }

    [Fact]
    public void ValidateAdjustment_NonPositiveQuantity_BlocksAndPointsToQuantity()
    {
        var result = AdjustStockDialog.ValidateAdjustment("0", "Merma", StockMovementReason.Damage, "Observación válida");

        Assert.False(result.IsValid);
        Assert.Equal("quantity", result.FocusField);
    }

    [Fact]
    public async Task AdjustStock_WhenDialogRejected_DoesNotCallService()
    {
        var product = CreateProduct();
        var productService = new Mock<IProductService>();
        productService.Setup(s => s.GetByIdAsync(product.Id)).ReturnsAsync(product);
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(36.50m);
        var dialogService = new Mock<IDialogService>();
        dialogService
            .Setup(d => d.ShowAdjustStockDialogWithAudit(It.IsAny<ProductDto>()))
            .Returns(AdjustStockDialogResult.Cancelled);

        var vm = new InventoryViewModel(productService.Object, rateService.Object, new UserSession(), dialogService.Object);
        var item = new ProductItemViewModel(product, rateService.Object, null);

        await vm.AdjustStockCommand.ExecuteAsync(item);

        productService.Verify(s => s.AdjustStockAsync(
            It.IsAny<int>(),
            It.IsAny<decimal>(),
            It.IsAny<string>(),
            It.IsAny<StockMovementReason?>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AdjustStock_WhenDialogApproved_PropagatesReasonCodeAndObservation()
    {
        var product = CreateProduct();
        var productService = new Mock<IProductService>();
        productService.Setup(s => s.GetByIdAsync(product.Id)).ReturnsAsync(product);
        var rateService = new Mock<IExchangeRateService>();
        rateService.Setup(r => r.CurrentRate).Returns(36.50m);
        var dialogService = new Mock<IDialogService>();
        dialogService
            .Setup(d => d.ShowAdjustStockDialogWithAudit(It.IsAny<ProductDto>()))
            .Returns(new AdjustStockDialogResult(
                true,
                -3m,
                "Merma por empaque",
                StockMovementReason.Damage,
                "Caja aplastada en anaquel 3"));

        var vm = new InventoryViewModel(productService.Object, rateService.Object, new UserSession(), dialogService.Object);
        var item = new ProductItemViewModel(product, rateService.Object, null);

        await vm.AdjustStockCommand.ExecuteAsync(item);

        productService.Verify(s => s.AdjustStockAsync(
            product.Id,
            -3m,
            "Merma por empaque",
            StockMovementReason.Damage,
            "Caja aplastada en anaquel 3"), Times.Once);
    }

    [Fact]
    public async Task ProductService_AdjustStockAsync_SerializesNumericReasonCodeAndObservation()
    {
        string? capturedBody = null;
        var client = new HttpClient(new CapturingHandler(body => capturedBody = body))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        await service.AdjustStockAsync(42, -3m, "Merma por empaque", StockMovementReason.Damage, "Caja aplastada");

        Assert.NotNull(capturedBody);
        Assert.Contains("\"reasonCode\":2", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"observation\":\"Caja aplastada\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductService_AdjustStockAsync_LegacyCallWithoutAuditFields_SerializesNulls()
    {
        string? capturedBody = null;
        var client = new HttpClient(new CapturingHandler(body => capturedBody = body))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        await service.AdjustStockAsync(42, -3m, "Merma");

        Assert.NotNull(capturedBody);
        Assert.Contains("\"reasonCode\":null", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"observation\":null", capturedBody, StringComparison.Ordinal);
    }

    private static ProductDto CreateProduct() => new()
    {
        Id = 5,
        Name = "Harina P.A.N.",
        SKU = "759000123",
        Cost = 4.50m,
        CostPriceUSD = 4.50m,
        ProfitMarginRetail = 30m,
        ProfitPercentage = 30m,
        PriceUSD = 5.85m,
        PriceBsS = 213.53m,
        StockQuantity = 10m,
        IsActive = true
    };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Action<string> _onBody;

        public CapturingHandler(Action<string> onBody) => _onBody = onBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _onBody(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
