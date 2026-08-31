using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using Desktop.Client.Services;
using Inventory.Module.Data;
using Inventory.Module.EventHandlers;
using Inventory.Module.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;
using CashTransactionType = Sales.Module.Entities.CashTransactionType;
using CashTransactionSource = Sales.Module.Entities.CashTransactionSource;

namespace CommandCenter.Tests;

public class FinancialRobustnessTests
{
    private SalesDbContext GetInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private (SalesService service, Mock<IMediator> mediator, Mock<ICashDrawerService> cashDrawer) CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        return (service, mockMediator, mockCashDrawer);
    }

    [Fact]
    public async Task CompleteSaleAsync_WhenPaymentExceedsTotal_LogsInformationAndCompletesSuccessfully()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 1,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        // Pago de 110 USD cuando la venta es de 100 USD (sobrepago / pago con vuelto)
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 110m, 5500m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);
        var savedSale = await context.Sales.FindAsync(sale.Id);

        Assert.NotNull(savedSale);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.True(invoiceNum > 0);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithExactPayment_CompletesSuccessfully()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 2,
            TotalUSD = 75.50m,
            Subtotal = 75.50m,
            AppliedRate = 50m,
            TotalBsS = 3775m,
            SubtotalBsS = 3775m,
            Status = SaleStatus.Pending,
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 1, ProductId = 10, Quantity = 2, UnitPrice = 37.75m }
            }
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Punto de Venta", IsCash = false });
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 75.50m, 3775m, "REF-1234")
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);

        var savedSale = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(savedSale);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.True(invoiceNum > 0);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task CompleteSaleAsync_WithRoundingTolerance_CompletesWithinFiveCents()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, _, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 3,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        // Paga 99.96 USD (diferencia de 0.04 USD <= 0.05 tolerancia)
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 99.96m, 4998m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);
        var savedSale = await context.Sales.FindAsync(sale.Id);

        Assert.NotNull(savedSale);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.True(invoiceNum > 0);
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_PartialPayment_RemainsOnHold()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 4,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.OnHold,
            Payments = new List<SalePayment>()
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false });
        await context.SaveChangesAsync();

        var request = new AddPaymentRequestDto
        {
            PaymentMethodId = 2,
            AmountUSD = 30m,
            AmountBsS = 1500m,
            ExchangeRate = 50m,
            ReferenceNumber = "REF-PARTIAL"
        };

        var result = await service.AddPaymentToHoldSaleAsync(sale.Id, request);

        var savedSale = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal("OnHold", result.Status);
        Assert.Equal(SaleStatus.OnHold, savedSale.Status);
        Assert.Single(savedSale.Payments);
        Assert.Equal(30m, savedSale.Payments[0].Amount);

        // SaleMadeEvent NO debe haberse publicado
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_ExceedsTotal_Throws()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, _, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 5,
            TotalUSD = 50m,
            AppliedRate = 50m,
            Status = SaleStatus.OnHold,
            Payments = new List<SalePayment>
            {
                new SalePayment { Amount = 40m, AmountBsS = 2000m, PaymentMethodId = 1 }
            }
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Punto de Venta", IsCash = false });
        await context.SaveChangesAsync();

        // Intento de abonar 20 USD cuando solo faltan 10 USD
        var request = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 20m,
            AmountBsS = 1000m,
            ExchangeRate = 50m
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddPaymentToHoldSaleAsync(sale.Id, request));
        Assert.Contains("excede el total pendiente", ex.Message);
    }

    [Fact]
    public async Task AddPaymentToHoldSaleAsync_WithCash_RegistersCashTransaction()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, _, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 6,
            TotalUSD = 100m,
            AppliedRate = 50m,
            Status = SaleStatus.OnHold,
            Payments = new List<SalePayment>()
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var request = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountUSD = 40m,
            AmountBsS = 2000m,
            ExchangeRate = 50m,
            ReferenceNumber = null
        };

        await service.AddPaymentToHoldSaleAsync(sale.Id, request);

        // Verificar que se haya registrado la CashTransaction para efectivo físico
        var cashTx = await context.CashTransactions.FirstOrDefaultAsync(ct => ct.SaleId == sale.Id);
        Assert.NotNull(cashTx);
        Assert.True(cashTx.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Income, cashTx.Type);
        Assert.Equal(CashTransactionSource.SalePayment, cashTx.Source);
        Assert.Equal(40m, cashTx.AmountUsd);
        Assert.Equal(2000m, cashTx.AmountLocal);
    }

    [Fact]
    public async Task HoldSaleAsync_WithFullInitialPayment_CompletesAndPublishesEvent()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var customer = new Customer { Id = 2, Name = "Maria Perez", CedulaOrRif = "V-20000000", IsDefault = false };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 7,
            TotalUSD = 50m,
            Subtotal = 50m,
            AppliedRate = 50m,
            TotalBsS = 2500m,
            SubtotalBsS = 2500m,
            Status = SaleStatus.Pending,
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 1, ProductId = 20, Quantity = 1, UnitPrice = 50m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var request = new HoldSaleRequestDto
        {
            CustomerId = customer.Id,
            ExchangeRate = 50m,
            InitialPayment = new AddPaymentRequestDto
            {
                PaymentMethodId = 1,
                AmountUSD = 50m,
                AmountBsS = 2500m,
                ExchangeRate = 50m
            }
        };

        var result = await service.HoldSaleAsync(sale.Id, request);

        var savedSale = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(savedSale);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.NotNull(savedSale.InvoiceNumber);

        // Publicación de SaleMadeEvent garantizada post-commit
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task HoldSaleAsync_WithFullInitialPayment_RollsBackOnDbFailure()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var customer = new Customer { Id = 3, Name = "Carlos Gomez", CedulaOrRif = "V-30000000", IsDefault = false };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 8,
            TotalUSD = 50m,
            AppliedRate = 50m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Configurar un cliente no existente para forzar fallo
        var invalidRequest = new HoldSaleRequestDto
        {
            CustomerId = 9999, // Inexistente
            ExchangeRate = 50m
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.HoldSaleAsync(sale.Id, invalidRequest));

        var unchangedSale = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(unchangedSale);
        Assert.Equal(SaleStatus.Pending, unchangedSale.Status);
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);
    }

    [Fact]
    public async Task ConfirmPickupAsync_DoesNotPublishSaleMadeEvent()
    {
        using var context = GetInMemorySalesDbContext();
        var (service, mediatorMock, _) = CreateSalesService(context);

        var customer = new Customer { Id = 4, Name = "Ana Ruiz", CedulaOrRif = "V-40000000", IsDefault = false };
        context.Customers.Add(customer);

        var sale = new Sale
        {
            Id = 9,
            CustomerId = 4,
            TotalUSD = 100m,
            AppliedRate = 50m,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            Items = new List<SaleItem>
            {
                new SaleItem { Id = 1, ProductId = 15, ProductName = "Arroz", Quantity = 10, UnitPrice = 10m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var historyResult = await service.ConfirmPickupAsync(sale.Id);

        var updatedSale = await context.Sales.FindAsync(sale.Id);
        Assert.NotNull(updatedSale);
        Assert.Equal(SaleDeliveryStatus.Delivered, updatedSale.DeliveryStatus);
        Assert.NotNull(updatedSale.PickupDate);

        // ConfirmPickup jamás debe publicar SaleMadeEvent (evita doble descuento de stock)
        mediatorMock.Verify(m => m.Publish(It.IsAny<SaleMadeEvent>(), default), Times.Never);
    }

    [Fact]
    public async Task ResilienceHandler_On4xx_DoesNotRetry()
    {
        var mockHealthService = new Mock<IHealthPollingService>();
        int requestAttempts = 0;

        var handler = new ResilienceHandler(mockHealthService.Object)
        {
            InnerHandler = new MockHttpMessageHandler((req, token) =>
            {
                Interlocked.Increment(ref requestAttempts);
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"VALIDATION_FAILED\",\"message\":\"Datos de entrada inválidos.\"}")
                };
                return Task.FromResult(response);
            })
        };

        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var response = await client.PostAsync("api/sales", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, requestAttempts); // Solo 1 intento, 0 reintentos ante 4xx
        mockHealthService.Verify(h => h.StartPolling(), Times.Never);
    }

    [Fact]
    public void HealthPollingService_StopPolling_IsIdempotent()
    {
        using var client = new HttpClient(new MockHttpMessageHandler((req, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        using var service = new HealthPollingService(client);

        // Detener múltiples veces seguidas no debe arrojar ninguna excepción ni estado inválido
        service.StopPolling();
        service.StopPolling();
        service.StopPolling();

        Assert.False(service.IsPollingActive);
    }

    [Fact]
    public async Task InventoryEventHandler_IdempotentDeduction_SkipsDuplicate()
    {
        using var db = GetInMemoryInventoryDbContext();
        var mockInventoryService = new Mock<IInventoryService>();

        var handler = new InventorySaleMadeEventHandler(mockInventoryService.Object, db);

        var saleEvent = new SaleMadeEvent(
            SaleId: 999,
            SaleDate: DateTime.UtcNow,
            Items: new List<SaleItemSnapshot>
            {
                new SaleItemSnapshot(ProductId: 101, Quantity: 5m)
            }
        );

        // Pre-insertar un registro de StockMovement para simular que esta venta ya fue deducida
        db.StockMovements.Add(new StockMovement
        {
            ProductId = 101,
            QuantityChange = -5m,
            NewStockLevel = 5m,
            Reason = "Sale #999",
            MovementDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Ejecutar el handler
        await handler.Handle(saleEvent, CancellationToken.None);

        // Como ya estaba procesado, UpdateStockAsync no debió ser llamado
        mockInventoryService.Verify(s => s.UpdateStockAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>()), Times.Never);
    }
}
