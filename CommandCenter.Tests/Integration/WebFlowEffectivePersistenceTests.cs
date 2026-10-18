using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.TestHelpers;
using Core.Constants;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.186-T5 (S4): integración del flujo de venta WEB de punta a punta sobre los servicios reales
/// que invocan los endpoints POST /api/sales (StartSaleAsync), POST /api/sales/{id}/items
/// (AddItemAsync), POST /api/sales/{id}/checkout-preview (CheckoutCalculationService) y
/// POST /api/sales/{id}/complete (CompleteSaleAsync). Verifica que la venta web persiste el monto
/// REAL transado SIN migración/columna nueva: TotalBsS efectivo por línea (política cash 8.185) +
/// AppliedRate snapshot + EffectiveTotalUSD derivado a 4dp (entidad Sale y mapping SaleDto), y que
/// el preview y la venta persistida no divergen (cero desfase), incluida la coherencia del cierre
/// Z (GetExpectedTotalsByPaymentMethodAsync suma SalePayments.AmountBsS efectivos).
/// </summary>
public class WebFlowEffectivePersistenceTests
{
    private const int ProductId = 907;
    // Tasa "real" alta (misma del precedente 8.185): expone el escalón de la política y la
    // derivación 4dp del USD efectivo sin caer en valores redondos que oculten el desfase.
    private const decimal Rate = 842.21m;
    // "Efectivo Bs.S" del seed estándar (IsCash = true): el efectivo exige montos enteros.
    private const int CashBsSMethodId = 2;

    /// <summary>Resultado del flujo web completo reutilizado por los cuatro escenarios del test.</summary>
    private sealed record WebFlowScenario(
        string DbName,
        SalesDbContext Context,
        SalesService SalesService,
        int SaleId,
        SaleDto SaleAfterAddItem,
        CheckoutPreviewResponse PreviewWithoutPayments,
        CheckoutPreviewResponse PreviewWithExactPayment,
        int InvoiceNumber);

    /// <summary>
    /// Ejecuta el flujo web espejo del controller real (start → add item → preview → completar),
    /// reemplazando solo la capa de inventario/BCV por un mock determinista (mismo patrón de
    /// SaleFlowIntegrationTests). La base InMemory es nueva por escenario (aislamiento por test).
    /// </summary>
    private static async Task<WebFlowScenario> RunWebFlowAsync(
        decimal priceUsd,
        decimal quantity,
        decimal rate,
        RoundingRuleType? productCashRule,
        string? globalCashRule,
        string? enforceFloorSetting)
    {
        var dbName = $"web_flow_8_186_{Guid.NewGuid():N}";
        var context = TestDatabaseFactory.CreateSalesDbContext(dbName);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        // DTO de inventario con la MISMA forma que resuelve InventoryService en producción
        // (Product.RoundingRuleCashBs → SaleProductInfoDto.RoundingRuleCashBs).
        var productInfo = new SaleProductInfoDto
        {
            Id = ProductId,
            Name = "Producto Web Redondeo",
            IsActive = true,
            IsDeleted = false,
            PriceUSD = priceUsd,
            PriceRetailUSD = priceUsd,
            PriceWholesaleUSD = priceUsd,
            PriceBsS = PricingCalculator.ToBsSCeiling(priceUsd, rate),
            HasWholesale = false,
            MinWholesaleQuantity = 6m,
            IsFractional = false,
            UnitOfMeasure = Core.Entities.UnitOfMeasureType.Und,
            CostPriceUSD = priceUsd * 0.5m,
            RoundingRuleCashBs = productCashRule
        };

        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetSaleProductByIdAsync(ProductId, It.IsAny<CancellationToken>())).ReturnsAsync(productInfo);
        inventory.Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SaleProductInfoDto> { productInfo });
        // Anclaje BCV determinista: la tasa del cliente coincide con la oficial ⇒ sin anclaje forzado.
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rate);

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultCashBsRoundingRule)).ReturnsAsync(globalCashRule);
        settings.Setup(s => s.GetSettingAsync(SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)).ReturnsAsync(enforceFloorSetting);

        var cashDrawer = new CashDrawerService(context);
        await cashDrawer.OpenSessionAsync(2000m, rate);

        var salesService = new SalesService(context, inventory.Object, Mock.Of<IMediator>(), cashDrawer, settings.Object);

        // POST /api/sales
        var started = await salesService.StartSaleAsync(cashierId: 7);
        // POST /api/sales/{id}/items
        var afterAdd = await salesService.AddItemAsync(started.Id, ProductId, quantity, rate);

        var calculator = new CheckoutCalculationService(salesService, settings.Object);
        // POST /api/sales/{id}/checkout-preview (sin pagos: así se lee el TotalBsS efectivo publicado)
        var previewWithoutPayments = await calculator.CalculatePreviewAsync(
            started.Id,
            new CheckoutPreviewRequest { ExchangeRate = rate });
        // POST /api/sales/{id}/checkout-preview (pago exacto del efectivo publicado)
        var previewWithExactPayment = await calculator.CalculatePreviewAsync(
            started.Id,
            new CheckoutPreviewRequest
            {
                ExchangeRate = rate,
                Payments = new List<SalePaymentDto>
                {
                    new() { PaymentMethodId = CashBsSMethodId, AmountBsS = previewWithoutPayments.TotalBsS }
                }
            });

        // POST /api/sales/{id}/complete pagando EXACTAMENTE preview.TotalBsS.
        var invoiceNumber = await salesService.CompleteSaleAsync(
            started.Id,
            rate,
            new List<PaymentInfo> { new(CashBsSMethodId, 0m, previewWithoutPayments.TotalBsS, null) });

        return new WebFlowScenario(
            dbName, context, salesService, started.Id, afterAdd, previewWithoutPayments, previewWithExactPayment, invoiceNumber);
    }

    /// <summary>
    /// Recarga la venta desde un contexto NUEVO sobre la MISMA base InMemory: prueba lectura real
    /// del store persistido (patrón OutboxProcessorJobTests), no del ChangeTracker del servicio.
    /// </summary>
    private static async Task<Sale> ReloadSaleAsync(string dbName, int saleId)
    {
        await using var verifyContext = TestDatabaseFactory.CreateSalesDbContext(dbName);
        return await verifyContext.Sales
            .AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstAsync(s => s.Id == saleId);
    }

    [Fact]
    public async Task WebFlow_WithProductCashRule_PersistsEffectiveLineTotalsAndDerivedUsd_AndMapsSaleDto()
    {
        // Override por artículo Step10; la regla GLOBAL queda null (ExactMath) para probar que el
        // override del artículo manda y viaja al snapshot de la línea.
        var scenario = await RunWebFlowAsync(
            priceUsd: 1.30m,
            quantity: 2m,
            rate: Rate,
            productCashRule: RoundingRuleType.VesCashFriendlyStep10,
            globalCashRule: null,
            enforceFloorSetting: "True");

        // Tras agregar la línea, el DTO ya refleja la regla y el Bs.S efectivo.
        var afterAddItem = Assert.Single(scenario.SaleAfterAddItem.Items);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, afterAddItem.RoundingRuleCashBs);
        Assert.Equal(1100.00m, afterAddItem.UnitPriceBsS);
        Assert.Equal(2200.00m, scenario.SaleAfterAddItem.TotalBsS);

        var persisted = await ReloadSaleAsync(scenario.DbName, scenario.SaleId);

        // Línea persistida con la regla resuelta y el Bs.S EFECTIVO del escalón:
        // 1.30 × 842.21 = 1094.873 → techo directo al paso 10 (nunca hacia abajo) = 1100.00 × 2 = 2200.00.
        var item = Assert.Single(persisted.Items);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, item.RoundingRuleCashBs);
        Assert.Equal(1100.00m, item.UnitPriceBsS);
        Assert.Equal(2200.00m, item.SubtotalBsS);

        // Snapshot de la venta: canónico intacto + total efectivo + tasa anclada.
        Assert.Equal(2.60m, persisted.TotalUSD);
        Assert.Equal(2200.00m, persisted.TotalBsS);
        Assert.Equal(842.21m, persisted.AppliedRate);
        Assert.Equal(SaleStatus.Completed, persisted.Status);
        Assert.Equal(scenario.InvoiceNumber, persisted.InvoiceNumber!.Value);

        // EffectiveTotalUSD DERIVADO (sin columna): round(2200 / 842.21, 4) = 2.6122 — incluye la
        // sobre-ganancia del escalón; el TotalUSD canónico queda como referencia de precios/gates.
        decimal expectedEffectiveUsd = Math.Round(2200.00m / 842.21m, 4, MidpointRounding.AwayFromZero);
        Assert.Equal(2.6122m, expectedEffectiveUsd);
        Assert.Equal(expectedEffectiveUsd, persisted.EffectiveTotalUSD);

        // Pago EXACTO del efectivo publicado: AmountBsS persistido = TotalBsS efectivo.
        var payment = Assert.Single(persisted.Payments);
        Assert.Equal(CashBsSMethodId, payment.PaymentMethodId);
        Assert.Equal(2200.00m, payment.AmountBsS);
        Assert.Equal(2200.00m, persisted.FinalPaidAmountBsS);

        // Coherencia con caja física: ingreso registrado por el monto efectivo real.
        var cashIncome = Assert.Single(
            scenario.Context.CashTransactions,
            t => t.SaleId == scenario.SaleId
                 && t.Type == CashTransactionType.Income
                 && t.Source == CashTransactionSource.SalePayment
                 && t.IsPhysicalCash);
        Assert.Equal(2200.00m, cashIncome.AmountLocal);

        // Mapping SaleDto: expone los MISMOS valores (EffectiveTotalUSD + TotalBsS + AppliedRate +
        // línea efectiva y pagos), sin recalcular con tasa actual.
        var dto = await scenario.SalesService.GetSaleAsync(scenario.SaleId);
        Assert.Equal(expectedEffectiveUsd, dto.EffectiveTotalUSD);
        Assert.Equal(persisted.EffectiveTotalUSD, dto.EffectiveTotalUSD);
        Assert.Equal(2200.00m, dto.TotalBsS);
        Assert.Equal(842.21m, dto.AppliedRate);
        var itemDto = Assert.Single(dto.Items);
        Assert.Equal(1100.00m, itemDto.UnitPriceBsS);
        Assert.Equal(2200.00m, itemDto.SubtotalBsS);
        Assert.Equal(RoundingRuleType.VesCashFriendlyStep10, itemDto.RoundingRuleCashBs);
        Assert.Equal(2200.00m, Assert.Single(dto.Payments).AmountBsS);
    }

    [Fact]
    public async Task WebFlow_CheckoutPreviewAndPersistedSale_AgreeWithZeroDrift()
    {
        var scenario = await RunWebFlowAsync(
            priceUsd: 1.30m,
            quantity: 2m,
            rate: Rate,
            productCashRule: RoundingRuleType.VesCashFriendlyStep10,
            globalCashRule: null,
            enforceFloorSetting: "True");

        var persisted = await ReloadSaleAsync(scenario.DbName, scenario.SaleId);

        // Identidad contable del preview: EffectiveTotalUSD == round(TotalBsS efectivo / tasa, 4).
        Assert.Equal(2200.00m, scenario.PreviewWithoutPayments.TotalBsS);
        Assert.Equal(2.6122m, scenario.PreviewWithoutPayments.EffectiveTotalUSD);

        // Cero desfase preview ↔ venta persistida: el "Gran Total $" del checkout web es el real.
        Assert.Equal(persisted.TotalBsS, scenario.PreviewWithoutPayments.TotalBsS);
        Assert.Equal(persisted.EffectiveTotalUSD, scenario.PreviewWithoutPayments.EffectiveTotalUSD);
        Assert.Equal(persisted.EffectiveTotalUSD, scenario.PreviewWithExactPayment.EffectiveTotalUSD);

        // El preview con el pago exacto queda 100% cubierto, sin remanente y sin vuelto.
        Assert.True(scenario.PreviewWithExactPayment.IsFullyPaid);
        Assert.Equal(2200.00m, scenario.PreviewWithExactPayment.TotalPaidBsS);
        Assert.Equal(0m, scenario.PreviewWithExactPayment.RemainingBalanceBsS);
        Assert.Equal(0m, scenario.PreviewWithExactPayment.RemainingBalanceUSD);
        Assert.Equal(0m, scenario.PreviewWithExactPayment.ChangeDueBsS);
    }

    [Fact]
    public async Task WebFlow_PersistedEffectivePayment_IsReflectedExactlyInDailyClosureExpectedTotals()
    {
        var scenario = await RunWebFlowAsync(
            priceUsd: 1.30m,
            quantity: 2m,
            rate: Rate,
            productCashRule: RoundingRuleType.VesCashFriendlyStep10,
            globalCashRule: null,
            enforceFloorSetting: "True");

        // El cierre Z suma SalePayments.AmountBsS (monto REAL transado) de la venta completada.
        await using var closureContext = TestDatabaseFactory.CreateSalesDbContext(scenario.DbName);
        var closureService = DailyClosureTestHelper.CreateService(closureContext);

        var totals = await closureService.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        var cashBsSTotal = Assert.Single(totals, t => t.PaymentMethodId == CashBsSMethodId);
        Assert.Equal(2200.00m, cashBsSTotal.ExpectedAmountBsS);

        // El esperado del Z es exactamente el efectivo persistido de la venta (cero descuadre).
        var persisted = await ReloadSaleAsync(scenario.DbName, scenario.SaleId);
        Assert.Equal(persisted.Payments.Sum(p => p.AmountBsS), cashBsSTotal.ExpectedAmountBsS);
        Assert.Equal(persisted.TotalBsS, cashBsSTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task WebFlow_WithoutCashRule_EffectiveEqualsCanonicalToday()
    {
        // Sin override por artículo ni política global (settings null) la regla resuelta es
        // ExactMath: el Bs.S efectivo debe ser EXACTAMENTE el canónico (techo 8.104 por línea) y
        // el USD efectivo, el canónico (fallback sin sobre-ganancia) — regresión S7 de 8.185.
        var scenario = await RunWebFlowAsync(
            priceUsd: 1.20m,
            quantity: 2m,
            rate: 50.00m,
            productCashRule: null,
            globalCashRule: null,
            enforceFloorSetting: null);

        var persisted = await ReloadSaleAsync(scenario.DbName, scenario.SaleId);

        var item = Assert.Single(persisted.Items);
        Assert.Equal(RoundingRuleType.ExactMath, item.RoundingRuleCashBs);
        Assert.Equal(60.00m, item.UnitPriceBsS); // techo canónico de 1.20 × 50.00
        Assert.Equal(120.00m, item.SubtotalBsS);

        Assert.Equal(2.40m, persisted.TotalUSD);
        Assert.Equal(50.00m, persisted.AppliedRate);
        // Regla canónica vigente: TotalBsS == round(TotalUSD × tasa, 2) (con estos valores el techo
        // por línea coincide con el redondeo).
        Assert.Equal(
            Math.Round(persisted.TotalUSD * persisted.AppliedRate, 2, MidpointRounding.AwayFromZero),
            persisted.TotalBsS);
        Assert.Equal(120.00m, persisted.TotalBsS);
        Assert.Equal(persisted.TotalUSD, persisted.EffectiveTotalUSD);

        // Preview espejo: mismo TotalBsS y el mismo USD efectivo que la venta persistida.
        Assert.Equal(120.00m, scenario.PreviewWithoutPayments.TotalBsS);
        Assert.Equal(2.40m, scenario.PreviewWithoutPayments.EffectiveTotalUSD);
        Assert.Equal(persisted.EffectiveTotalUSD, scenario.PreviewWithoutPayments.EffectiveTotalUSD);
    }
}
