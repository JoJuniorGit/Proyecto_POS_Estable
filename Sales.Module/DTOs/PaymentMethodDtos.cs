using System.ComponentModel.DataAnnotations;
using Sales.Module.Entities;

namespace Sales.Module.DTOs;

public class CreatePaymentMethodDto
{
    [Required(ErrorMessage = "El nombre del método de pago es obligatorio.")]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool RequiresReference { get; set; }
    public bool IsCash { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Opcional: USD/Bs.S. Sin valor se deriva del nombre (regla histórica).</summary>
    [MaxLength(10)]
    public string? Currency { get; set; }

    /// <summary>
    /// P0 (S1): comisión opcional del medio de pago. Se persiste como <c>PaymentMethodFeeRule</c>
    /// activa en la misma operación lógica; una comisión 0/0 (o el campo ausente) no crea regla.
    /// La comisión es un costo del negocio y nunca se suma al cliente.
    /// </summary>
    public PaymentMethodCommissionDto? Commission { get; set; }
}

/// <summary>
/// P0 (S1): apartado opcional de comisión del alta de medios de pago. Reutiliza el contrato de
/// <see cref="PaymentMethodFeeRule"/>: tipo, componentes, moneda y vigencia.
/// </summary>
public class PaymentMethodCommissionDto
{
    public PaymentMethodFeeType FeeType { get; set; } = PaymentMethodFeeType.Percentage;

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    [MaxLength(10)]
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>Vigencia desde; si se omite (o llega en cero) la regla rige desde hoy 00:00 UTC.</summary>
    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;
}

public class UpdatePaymentMethodDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "El nombre del método de pago es obligatorio.")]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool RequiresReference { get; set; }
    public bool IsCash { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Opcional: null/omitido conserva la moneda persistida; un cambio explícito se rechaza.</summary>
    [MaxLength(10)]
    public string? Currency { get; set; }
}

public class PaymentMethodDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool RequiresReference { get; set; }
    public bool IsCash { get; set; }
    public string Currency { get; set; } = "Bs.S";
    public int DisplayOrder { get; set; }
    public bool IsDeleted { get; set; }
}
