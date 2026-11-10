namespace Core.DTOs;

/// <summary>
/// 8.211-T3c3a (S11): fresh-install onboarding snapshot for the monetary profile.
/// <see cref="CanConfigureMonetaryProfile"/> is false once financial data exists or when the
/// transition guard is unavailable; presentation symbols remain editable regardless.
/// </summary>
public sealed class MonetarySetupStatusDto
{
    public bool IsConfigured { get; set; }

    public bool CanConfigureMonetaryProfile { get; set; }

    public MonetaryProfileDto Profile { get; set; } = new();
}
