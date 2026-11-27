using System;
using System.Globalization;

namespace Desktop.Client.Services;

/// <summary>
/// T4 (Paso 13 / 8.188, S1/S4/S5): espejo cliente de los enums persistidos del servidor. Los
/// ids son los mismos valores guardados en la base; nunca se renumeran.
/// </summary>
public enum PettyCashCurrencyClient
{
    BsS = 0,
    Usd = 1
}

public enum PettyCashCategoryClient
{
    InsumosLimpieza = 0,
    FletesAcarreo = 1,
    MantenimientoReparaciones = 2,
    ServiciosPublicos = 3,
    Papeleria = 4,
    Varios = 5
}

public enum PettyCashStatusClient
{
    Active = 0,
    Cancelled = 1
}

/// <summary>
/// T4 (S1): payload del registro. El cliente solo envía el nominal de la divisa física que sale
/// de la gaveta; la tasa BCV, el contravalor dual y la liquidez los resuelve el servidor.
/// </summary>
public sealed class PettyCashExpenseClientRequest
{
    public PettyCashCurrencyClient Currency { get; set; }

    public decimal Amount { get; set; }

    public PettyCashCategoryClient Category { get; set; }

    public string? Concept { get; set; }

    public string? Beneficiary { get; set; }

    public string? ReferenceNumber { get; set; }

    /// <summary>Clave de idempotencia del registro (replay sin doble movimiento).</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>T4 (S5): solicitud de anulación lógica con motivo obligatorio.</summary>
public sealed class PettyCashCancelClientRequest
{
    public string? Reason { get; set; }
}

/// <summary>
/// T4 (S1/S5/S7): gasto de caja chica expuesto por el servidor. Conserva los snapshots
/// bimonetarios congelados, el correlativo del vale y el estado de anulación.
/// </summary>
public sealed class PettyCashExpenseClientDto
{
    public int Id { get; set; }

    public long VoucherNumber { get; set; }

    public string VoucherCode => $"VCC-{VoucherNumber:D6}";

    public int SessionId { get; set; }

    public PettyCashCategoryClient Category { get; set; }

    public PettyCashCurrencyClient Currency { get; set; }

    public decimal AmountBsS { get; set; }

    public decimal AmountUsd { get; set; }

    public decimal HistoricalRate { get; set; }

    public string Concept { get; set; } = string.Empty;

    public string Beneficiary { get; set; } = string.Empty;

    public string ReferenceNumber { get; set; } = string.Empty;

    public int CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public int? AuthorizedByUserId { get; set; }

    public int? AuthorizationRequestId { get; set; }

    public PettyCashStatusClient Status { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    public int? CancelledByUserId { get; set; }

    public string? CancellationReason { get; set; }

    public int? CashTransactionId { get; set; }

    public bool IsReplay { get; set; }
}

/// <summary>T4 (S2): saldos físicos teóricos del turno, segregados por moneda.</summary>
public sealed class PettyCashPhysicalBalancesClientDto
{
    public decimal BsS { get; set; }

    public decimal Usd { get; set; }
}

/// <summary>
/// T4 (S4): 403 del contrato de caja chica. Conserva el contexto exacto (sesión, moneda, montos
/// bimonetarios y categoría) que el hub debe reenviar para ligar el token de autorización.
/// </summary>
public sealed class PettyCashAuthorizationException : Exception
{
    public PettyCashAuthorizationException(string? message, PettyCashExpenseAuthorizationRequestContext context)
        : base(string.IsNullOrWhiteSpace(message) ? "Se requiere autorización de caja chica." : message)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public PettyCashExpenseAuthorizationRequestContext Context { get; }

    public string AuthorizationAction => "PettyCashExpense";
}

/// <summary>Traduce los enums del gasto a las etiquetas formales en español de la UI.</summary>
public static class PettyCashLabels
{
    public static string Currency(PettyCashCurrencyClient currency) =>
        currency == PettyCashCurrencyClient.Usd
            ? CurrencyDisplay.PrimaryCurrencyCode
            : CurrencyDisplay.LocalCurrencySymbol;

    public static string Category(PettyCashCategoryClient category) => category switch
    {
        PettyCashCategoryClient.InsumosLimpieza => "Insumos y Limpieza",
        PettyCashCategoryClient.FletesAcarreo => "Fletes y Acarreo",
        PettyCashCategoryClient.MantenimientoReparaciones => "Mantenimiento y Reparaciones",
        PettyCashCategoryClient.ServiciosPublicos => "Servicios Públicos/Eventuales",
        PettyCashCategoryClient.Papeleria => "Papelería",
        _ => "Varios"
    };

    public static string Status(PettyCashStatusClient status) =>
        status == PettyCashStatusClient.Cancelled ? "Anulado" : "Activo";

    /// <summary>Bs.S siempre a 2 decimales, honrando el formato de moneda configurado.</summary>
    public static string FormatBsS(decimal amount) => CurrencyDisplay.Number(amount, 2);

    /// <summary>8.211-T6-U7: the legacy AmountUsd is presented as the profile primary currency.</summary>
    public static string FormatUsd(decimal amount) =>
        CurrencyDisplay.FormatPrimary(amount, 4);
}


