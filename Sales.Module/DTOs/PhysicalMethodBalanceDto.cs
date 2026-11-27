namespace Sales.Module.DTOs;

/// <summary>
/// 8.212-WU2: physical cash drawer balance for one payment method captured when the session
/// opened. <see cref="CurrencyCode"/> is the frozen drawer snapshot; <see cref="CurrencySymbol"/>
/// comes from the active monetary profile and falls back to the code. <see cref="CanDispense"/>
/// is an operational hint, never a replacement for the server-side balance validation.
/// </summary>
public sealed record PhysicalMethodBalanceDto
{
    public int PaymentMethodId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CurrencyCode { get; init; } = string.Empty;
    public string CurrencySymbol { get; init; } = string.Empty;
    public decimal CurrentBalance { get; init; }
    public bool IsActive { get; init; }
    public bool CanDispense { get; init; }
}
