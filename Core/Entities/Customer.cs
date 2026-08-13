using System.ComponentModel.DataAnnotations;

namespace Core.Entities;

public class Customer : BaseEntity
{
    [Required]
    [MaxLength(20)]
    public string CedulaOrRif { get; set; } = string.Empty;

    [Required]
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    public decimal CreditLimitUSD { get; set; } = 0m;

    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; } = false;
}
