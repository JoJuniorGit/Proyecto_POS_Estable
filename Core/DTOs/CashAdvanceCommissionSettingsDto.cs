namespace Core.DTOs;

/// <summary>
/// Commission percentages charged by the cash advance channels. Values are percentages in the
/// 0..100 range (for example 7 means 7%). Zero means "not configured" and the coordinator rejects
/// the advance until an Admin saves both channels.
/// </summary>
public sealed class CashAdvanceCommissionSettingsDto
{
    public decimal TransferCommissionPct { get; set; }
    public decimal CashCommissionPct { get; set; }
}
