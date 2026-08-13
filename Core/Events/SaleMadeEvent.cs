using System;
using System.Collections.Generic;
using MediatR;

namespace Core.Events;

public record SaleMadeEvent(
    int SaleId,
    DateTime SaleDate,
    IEnumerable<SaleItemSnapshot> Items) : INotification;

public record SaleItemSnapshot(
    int ProductId,
    decimal Quantity);
