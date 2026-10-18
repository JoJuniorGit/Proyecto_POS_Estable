using Core.Entities;

namespace Core.DTOs;

/// <summary>
/// Paso 11 (S1): perfil crediticio observable del cliente. El Crédito Disponible y el
/// Porcentaje de Utilización se calculan en tiempo real (no se persisten): Disponible =
/// Límite autorizado − Saldo deudor actual de obligaciones abiertas.
/// </summary>
public class CreditProfileDto
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsCreditEnabled { get; set; }

    /// <summary>Estado de bloqueo vigente (Active / SuspendedByMora / ManuallyBlocked).</summary>
    public CreditStatus CreditStatus { get; set; }

    public string? CreditNotes { get; set; }

    /// <summary>Cupo máximo de deuda simultánea autorizada (USD).</summary>
    public decimal CreditLimitUSD { get; set; }

    /// <summary>Suma de BalanceUSD de obligaciones abiertas con saldo &gt; 0.005 (USD 4dp).</summary>
    public decimal CurrentDebtUSD { get; set; }

    /// <summary>Límite − saldo deudor; puede ser negativo para reportería (elegibilidad lo bloquea).</summary>
    public decimal AvailableCreditUSD { get; set; }

    /// <summary>Saldo deudor / límite × 100 (2dp). Sin límite positivo se reporta 0 para no dividir por cero.</summary>
    public decimal UtilizationPercent { get; set; }

    /// <summary>Plazo estándar de pago en días continuos desde la emisión.</summary>
    public int PaymentTermDays { get; set; }

    /// <summary>Días de gracia posteriores al vencimiento antes de considerar mora.</summary>
    public int CreditGraceDays { get; set; }

    /// <summary>Suma de saldos vencidos fuera del período de gracia (USD 4dp).</summary>
    public decimal OverdueUSD { get; set; }

    /// <summary>Cantidad de obligaciones abiertas vencidas fuera del período de gracia.</summary>
    public int OverdueCount { get; set; }
}

/// <summary>
/// Resultado estructurado de la evaluación de solvencia en tiempo real (S2/S4): elegibilidad
/// del monto a financiar con los bloqueos aplicables en español y el cupo disponible calculado.
/// F2a NO admite sobregiro: exceder el disponible bloquea (T3 agrega la autorización supervisor).
/// </summary>
public class CreditEligibilityResultDto
{
    public bool IsEligible { get; set; }

    /// <summary>Motivos de bloqueo en español (vacío cuando IsEligible es true).</summary>
    public System.Collections.Generic.List<string> Blockers { get; set; } = new();

    public decimal RequestedAmountUSD { get; set; }
    public decimal CreditLimitUSD { get; set; }
    public decimal CurrentDebtUSD { get; set; }
    public decimal AvailableCreditUSD { get; set; }
}
