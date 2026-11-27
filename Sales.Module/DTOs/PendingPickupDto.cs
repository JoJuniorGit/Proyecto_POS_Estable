using System;
using System.Collections.Generic;

namespace Sales.Module.DTOs;

public class PendingPickupDto
{
    public int SaleId { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerCedula { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public decimal AppliedRate { get; set; }

    /// <summary>
    /// Sincronizacion USD efectivo (ANEXO XV): inverso del Bs.S EFECTIVO a la tasa anclada —
    /// el $ de las cuentas abiertas/retiros es el monto realmente transado, no la suma canonica
    /// de precios base. Se serializa a los clientes (web/WPF derivan local como respaldo).
    /// </summary>
    public decimal EffectiveTotalUSD => TotalBsS <= 0m || AppliedRate <= 0m
        ? TotalUSD
        : Math.Round(TotalBsS / AppliedRate, 4, MidpointRounding.AwayFromZero);

    public decimal TotalUnits { get; set; }
    public decimal DeliveredUnits { get; set; }
    public string DeliveryStatus { get; set; } = "PendingPickup";
    public DateTime? PickupDate { get; set; }
    public List<PendingPickupItemDto> Items { get; set; } = new();
}
