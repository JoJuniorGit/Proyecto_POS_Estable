using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Core.Entities;
using Core.Helpers;

namespace Sales.Module.Entities;

public enum SaleStatus
{
    Pending,
    Completed,
    Cancelled,
    OnHold
}

public class Sale
{
    public int Id { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    
    [MaxLength(150)]
    public string? CustomerName { get; set; }
    
    [MaxLength(20)]
    public string? CustomerCedula { get; set; }
    
    public SaleDeliveryStatus DeliveryStatus { get; set; } = SaleDeliveryStatus.Delivered;
    public DateTime? PickupDate { get; set; }

    [MaxLength(20)]
    public string PriceListType { get; set; } = "Retail";

    /// <summary>
    /// Consecutive invoice number assigned only upon sale completion.
    /// Independent of the PK to avoid gaps from pending/cancelled sales.
    /// </summary>
    public int? InvoiceNumber { get; set; }

    public DateTime Date { get; set; } = DateTime.UtcNow;

    [Required]
    public SaleStatus Status { get; set; } = SaleStatus.Pending;

    [Column(TypeName = "decimal(18,4)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalUSD { get; set; }
    
    [Column(TypeName = "decimal(18,4)")]
    public decimal AppliedRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalBsS { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal SubtotalBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal RoundingAdjustment { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FinalPaidAmountBsS { get; set; }

    // 8.211-T3a (S6): snapshot aditivo del perfil monetario vigente al completar la venta.
    // Instalaciones legacy o sin perfil configurado conservan USD/VES/Dual. Los montos y tasas
    // históricos siguen siendo la fuente de cálculo: estas columnas solo describen el régimen.
    [MaxLength(3)]
    public string PrimaryCurrencyCode { get; set; } = "USD";

    [MaxLength(3)]
    public string? SecondaryCurrencyCode { get; set; } = "VES";

    [MaxLength(16)]
    public string MonetaryMode { get; set; } = "Dual";

    // 8.211-T3c2 (S6): snapshots aditivos de los importes del régimen USD-primario vigente.
    // Duplican los totales canónicos (TotalUSD/TotalBsS) sin recalcularlos ni reinterpretarlos;
    // las columnas históricas siguen siendo la fuente de cálculo. En esta fase la única ruta
    // habilitada es Dual USD/VES y la tasa proviene de BCV (AppliedRateSource = "Bcv").
    [Column(TypeName = "decimal(18,4)")]
    public decimal? PrimaryAmount { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? SecondaryAmount { get; set; }

    [MaxLength(16)]
    public string? AppliedRateSource { get; set; }

    public int? CashierId { get; set; }
    public User? Cashier { get; set; }

    public int? ClaimedByUserId { get; set; }

    [MaxLength(150)]
    public string? ClaimedByUserName { get; set; }

    public SaleClaimAction ClaimAction { get; set; } = SaleClaimAction.None;

    public DateTime? ClaimedAtUtc { get; set; }

    public bool IsZeroAmountOrder => TotalUSD == 0;

    /// <summary>
    /// Sincronización USD efectivo (ANEXO XII): monto real transado en USD — inverso del Total
    /// Bs.S EFECTIVO (ya redondeado) a la tasa anclada de la venta. Propiedad calculada, sin
    /// columna nueva: TotalBsS + AppliedRate ya son el snapshot inmutable y duplicar el cociente
    /// abriría una segunda fuente de verdad (el flujo de venta lo persiste como derivado).
    /// 4 decimales por consistencia con el precedente de CashAdvance (BsS/rate) y precisión
    /// sub-centavo para analítica (Paso 14). Sin Bs.S cobrado o sin tasa cae al canónico.
    /// 8.186-T3 (S3): la fórmula vive en el helper único PricingCalculator.EffectiveUsdFromBsS
    /// (compartido con el checkout preview web) — comportamiento byte-idéntico, sin duplicar la inversa.
    /// </summary>
    [NotMapped]
    public decimal EffectiveTotalUSD => PricingCalculator.EffectiveUsdFromBsS(TotalBsS, AppliedRate, TotalUSD);

    public List<SalePayment> Payments { get; set; } = new();

    // Navigation property
    public List<SaleItem> Items { get; set; } = new();
}
