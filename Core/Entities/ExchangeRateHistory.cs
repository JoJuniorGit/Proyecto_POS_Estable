using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Entities;

public class ExchangeRateHistory
{
    [Key]
    [Column(TypeName = "date")]
    public DateOnly Date { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal Rate { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
