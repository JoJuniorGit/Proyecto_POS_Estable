using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Events;
using Logistics.Module.DTOs;
using Logistics.Module.EventHandlers;
using Logistics.Module.Events;
using Logistics.Module.Services;
using MediatR;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DeliveryServiceTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly DeliveryService _service;

    public DeliveryServiceTests()
    {
        _mediatorMock = new Mock<IMediator>();
        _service = new DeliveryService(_mediatorMock.Object);
    }

    [Fact]
    public async Task RegisterDeliveryOrder_Succeeds_AndCanBeRetrieved()
    {
        var order = new DeliveryOrderDto
        {
            OrderId = 101,
            OrderNumber = "FAC-000101",
            CustomerName = "Carlos Perez",
            CustomerPhone = "04141234567",
            DeliveryAddress = "Av. Bolivar, Edf. Centro, Piso 2",
            TotalAmount = 45.50m
        };

        var registered = await _service.RegisterDeliveryOrderAsync(order);

        Assert.NotNull(registered);
        Assert.Equal(101, registered.OrderId);

        var retrieved = await _service.GetDeliveryOrderAsync(101);
        Assert.NotNull(retrieved);
        Assert.Equal("Carlos Perez", retrieved.CustomerName);
        Assert.Equal(OrderStatus.Pending, retrieved.Status);
    }

    [Fact]
    public async Task RegisterDeliveryOrder_RejectsInvalidOrderId()
    {
        var order = new DeliveryOrderDto
        {
            OrderId = 0,
            OrderNumber = "FAC-000000"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _service.RegisterDeliveryOrderAsync(order));
    }

    [Fact]
    public async Task AssignDriver_UpdatesDriverAndStatus_AndPublishesDeliveryAssignedEvent()
    {
        var order = new DeliveryOrderDto
        {
            OrderId = 102,
            OrderNumber = "FAC-000102",
            CustomerName = "Maria Gomez",
            DeliveryAddress = "Calle 5 #10-20",
            TotalAmount = 25m,
            Status = OrderStatus.Preparing
        };
        await _service.RegisterDeliveryOrderAsync(order);

        await _service.AssignDriverAsync(102, driverId: 5);

        var retrieved = await _service.GetDeliveryOrderAsync(102);
        Assert.NotNull(retrieved);
        Assert.Equal(5, retrieved.DriverId);
        Assert.Equal(OrderStatus.OutForDelivery, retrieved.Status);

        _mediatorMock.Verify(m => m.Publish(
            It.Is<DeliveryAssignedEvent>(e => e.OrderId == 102 && e.DriverId == 5),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateDeliveryStatus_Delivered_SetsDeliveredAt_AndPublishesEvent()
    {
        var order = new DeliveryOrderDto
        {
            OrderId = 103,
            OrderNumber = "FAC-000103",
            CustomerName = "Ana Torres",
            DeliveryAddress = "Urb. Los Pinos, Casa 4",
            TotalAmount = 80m,
            Status = OrderStatus.OutForDelivery,
            DriverId = 5
        };
        await _service.RegisterDeliveryOrderAsync(order);

        await _service.UpdateDeliveryStatusAsync(103, OrderStatus.Delivered, "Entregado a cliente");

        var retrieved = await _service.GetDeliveryOrderAsync(103);
        Assert.NotNull(retrieved);
        Assert.Equal(OrderStatus.Delivered, retrieved.Status);
        Assert.NotNull(retrieved.DeliveredAtUtc);
        Assert.Contains("Entregado a cliente", retrieved.Notes);

        _mediatorMock.Verify(m => m.Publish(
            It.Is<DeliveryStatusUpdatedEvent>(e => e.OrderId == 103 && e.NewStatus == OrderStatus.Delivered && e.PreviousStatus == OrderStatus.OutForDelivery),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaleDispatchedEventHandler_IgnoresSaleWithoutDeliveryAddress()
    {
        var handler = new SaleDispatchedEventHandler(_service);
        var dispatchedEvent = new SaleDispatchedEvent(
            SaleId: 201,
            InvoiceNumber: "FAC-000201",
            CustomerName: "Juan Perez",
            DeliveryAddress: null, // Venta de mostrador
            CustomerPhone: null,
            TotalAmount: 15m,
            DispatchedAtUtc: DateTime.UtcNow);

        await handler.Handle(dispatchedEvent, CancellationToken.None);

        var retrieved = await _service.GetDeliveryOrderAsync(201);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task SaleDispatchedEventHandler_RegistersDeliveryOrder_WhenAddressPresent()
    {
        var handler = new SaleDispatchedEventHandler(_service);
        var dispatchedEvent = new SaleDispatchedEvent(
            SaleId: 202,
            InvoiceNumber: "FAC-000202",
            CustomerName: "Pedro Diaz",
            DeliveryAddress: "Sector Las Delicias, Callejón 3",
            CustomerPhone: "04129998877",
            TotalAmount: 95.20m,
            DispatchedAtUtc: DateTime.UtcNow);

        await handler.Handle(dispatchedEvent, CancellationToken.None);

        var retrieved = await _service.GetDeliveryOrderAsync(202);
        Assert.NotNull(retrieved);
        Assert.Equal("FAC-000202", retrieved.OrderNumber);
        Assert.Equal("Pedro Diaz", retrieved.CustomerName);
        Assert.Equal("Sector Las Delicias, Callejón 3", retrieved.DeliveryAddress);
        Assert.Equal(OrderStatus.Preparing, retrieved.Status);
    }
}
