using System.ComponentModel.DataAnnotations;

namespace Backend.API.DTOs;

/// <summary>
/// DTO de entrada para la creación de métodos de pago ([8B-CR1]).
/// </summary>
public class CreatePaymentMethodDto
{
    [Required(ErrorMessage = "El nombre del método de pago es obligatorio.")]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool RequiresReference { get; set; }
    public bool IsCash { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// DTO de entrada para la actualización de métodos de pago ([8B-CR1]).
/// </summary>
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
}
