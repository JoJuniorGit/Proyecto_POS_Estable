using System.ComponentModel.DataAnnotations;

namespace Sales.Module.Entities;

public class PaymentMethod
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public bool RequiresReference { get; set; } = false;
    public bool IsCash { get; set; } = false;
    public int DisplayOrder { get; set; } = 0;
}
