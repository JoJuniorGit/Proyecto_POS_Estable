using System;

namespace Sales.Module.Exceptions;

public class SaleLockedException : InvalidOperationException
{
    public int SaleId { get; }
    public int? ClaimedByUserId { get; }
    public string? ClaimedByUserName { get; }
    public string ClaimAction { get; }
    public DateTime? ClaimedAtUtc { get; }

    public SaleLockedException(
        int saleId,
        int? claimedByUserId,
        string? claimedByUserName,
        string claimAction,
        DateTime? claimedAtUtc)
        : base($"El pedido #{saleId} está bloqueado por {BuildDisplayName(claimedByUserName)} ({BuildActionLabel(claimAction)}).")
    {
        SaleId = saleId;
        ClaimedByUserId = claimedByUserId;
        ClaimedByUserName = claimedByUserName;
        ClaimAction = claimAction;
        ClaimedAtUtc = claimedAtUtc;
    }

    private static string BuildDisplayName(string? claimedByUserName) =>
        string.IsNullOrWhiteSpace(claimedByUserName) ? "otro cajero" : claimedByUserName;

    private static string BuildActionLabel(string claimAction) =>
        string.Equals(claimAction, "Checkout", StringComparison.Ordinal) ? "en proceso de pago" : "editando pedido";
}
