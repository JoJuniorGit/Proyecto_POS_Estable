using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// 8.9-M16: moneda efectiva del método derivada por el backend (fuente de verdad) y
    /// expuesta a los clientes para evitar heurísticas locales divergentes en el arqueo.
    /// Computada, no persistida: no requiere migración.
    /// </summary>
    [NotMapped]
    public string Currency => PaymentMethodCurrencyResolver.Resolve(Name);
}
