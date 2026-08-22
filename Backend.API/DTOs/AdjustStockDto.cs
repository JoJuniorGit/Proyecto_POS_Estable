using System.ComponentModel.DataAnnotations;

namespace Backend.API.DTOs;

public class AdjustStockDto
{
    [Required]
    public decimal QuantityChange { get; set; }

    [Required]
    public string Reason { get; set; } = string.Empty;
}
