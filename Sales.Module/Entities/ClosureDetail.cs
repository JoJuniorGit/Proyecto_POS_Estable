using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public class ClosureDetail
{
    public int Id { get; set; }

    [Required]
    public int DailyClosureId { get; set; }
    public DailyClosure? DailyClosure { get; set; }

    [Required]
    public int PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    [MaxLength(50)]
    public string PaymentMethodName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal ExpectedAmountBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal ActualAmountBsS { get; set; }

    /// <summary>
    /// DifferenceBsS = ActualAmountBsS - ExpectedAmountBsS
    /// Positive = excess, Negative = shortage
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal DifferenceBsS { get; set; }
}
