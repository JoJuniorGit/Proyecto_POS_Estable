using System.ComponentModel.DataAnnotations;

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
