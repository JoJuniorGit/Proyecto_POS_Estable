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
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// 8.201: moneda de la gaveta (USD/Bs.S) persistida. Null en filas legadas: el resolver cae
    /// a la regla histórica por nombre. Renombrar el método NO cambia este valor, de modo que los
    /// movimientos históricos conservan su gaveta.
    /// </summary>
    [MaxLength(10)]
    public string? Currency { get; set; }
}
