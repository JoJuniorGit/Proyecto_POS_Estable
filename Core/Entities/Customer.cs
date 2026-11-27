using System.ComponentModel.DataAnnotations;

namespace Core.Entities;

/// <summary>
/// Estado crediticio del cliente (Paso 11, S1). Valores estables persistidos como int;
/// los nuevos estados se agregan al final para no renumerar lo ya almacenado.
/// </summary>
public enum CreditStatus
{
    Active = 0,
    SuspendedByMora = 1,
    ManuallyBlocked = 2
}

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

    // Perfil crediticio (Paso 11, S1): habilitacion explicita, plazo/periodo de gracia en dias
    // y estado de bloqueo con traza. CreditLimitUSD conserva su semantica (cupo autorizado).
    public bool IsCreditEnabled { get; set; } = false;
    public int PaymentTermDays { get; set; } = 30;
    public int CreditGraceDays { get; set; } = 0;
    public CreditStatus CreditStatus { get; set; } = CreditStatus.Active;

    [MaxLength(1000)]
    public string? CreditNotes { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; } = false;
}
