using System;
using System.ComponentModel.DataAnnotations;

namespace Backend.API.DTOs;

public class ReserveStockDto
{
    [Required]
    public int ProductId { get; set; }

    [Required]
    public decimal Quantity { get; set; }

    [Required]
    public int DurationSeconds { get; set; }
}

public class ConfirmReservationDto
{
    [Required]
    public string Reason { get; set; } = string.Empty;
}
