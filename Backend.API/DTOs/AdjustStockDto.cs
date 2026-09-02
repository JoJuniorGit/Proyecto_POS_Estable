using System.ComponentModel.DataAnnotations;

namespace Backend.API.DTOs;

public class AdjustStockDto
{
    [Required(ErrorMessage = "La cantidad a ajustar es requerida.")]
    [Range(-1000000, 1000000, ErrorMessage = "La cantidad debe estar entre -1,000,000 y 1,000,000.")]
    public decimal QuantityChange { get; set; }

    [Required(ErrorMessage = "El motivo del ajuste es obligatorio.")]
    [MinLength(3, ErrorMessage = "El motivo debe tener al menos 3 caracteres.")]
    [MaxLength(200, ErrorMessage = "El motivo no puede exceder los 200 caracteres.")]
    public string Reason { get; set; } = string.Empty;
}
