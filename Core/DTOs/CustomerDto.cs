using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Core.DTOs;

public class CustomerDto
{
    public int Id { get; set; }
    public string CedulaOrRif { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public decimal CreditLimitUSD { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class CreateCustomerDto
{
    [Required(ErrorMessage = "La cédula o RIF es obligatoria.")]
    [StringLength(20, ErrorMessage = "La cédula o RIF no puede exceder 20 caracteres.")]
    [RegularExpression(@"^(?:[VJEPGvjepg]-?\d{6,9}(?:-\d)?|V-00000000|\d{6,9})$", ErrorMessage = "Formato de cédula o RIF inválido.")]
    public string CedulaOrRif { get; set; } = string.Empty;

    [Required(ErrorMessage = "El nombre del cliente es obligatorio.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 100 caracteres.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(20, ErrorMessage = "El teléfono no puede exceder 20 caracteres.")]
    [RegularExpression(@"^(?:0(?:412|414|416|424|426)\d{7}|\+?\d{10,15}|)$", ErrorMessage = "Formato de teléfono inválido.")]
    public string Phone { get; set; } = string.Empty;

    [Range(0, 1000000, ErrorMessage = "El límite de crédito no puede ser negativo ni exceder $1,000,000.")]
    public decimal CreditLimitUSD { get; set; } = 0m;
}

public class UpdateCustomerDto
{
    [Required(ErrorMessage = "La cédula o RIF es obligatoria.")]
    [StringLength(20, ErrorMessage = "La cédula o RIF no puede exceder 20 caracteres.")]
    [RegularExpression(@"^(?:[VJEPGvjepg]-?\d{6,9}(?:-\d)?|V-00000000|\d{6,9})$", ErrorMessage = "Formato de cédula o RIF inválido.")]
    public string CedulaOrRif { get; set; } = string.Empty;

    [Required(ErrorMessage = "El nombre del cliente es obligatorio.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "El nombre debe tener entre 2 y 100 caracteres.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(20, ErrorMessage = "El teléfono no puede exceder 20 caracteres.")]
    [RegularExpression(@"^(?:0(?:412|414|416|424|426)\d{7}|\+?\d{10,15}|)$", ErrorMessage = "Formato de teléfono inválido.")]
    public string Phone { get; set; } = string.Empty;

    [Range(0, 1000000, ErrorMessage = "El límite de crédito no puede ser negativo ni exceder $1,000,000.")]
    public decimal CreditLimitUSD { get; set; } = 0m;

    public bool IsActive { get; set; } = true;
}

public class CustomerPagedResultDto
{
    public List<CustomerDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
