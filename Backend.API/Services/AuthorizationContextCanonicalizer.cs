using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Backend.API.Services;

/// <summary>
/// 8.150 (T3, design D3): payload canonico de la operacion protegida. El mismo calculo corre al
/// crear la solicitud y al consumir el token (T5), de modo que el hash liga el token al payload
/// exacto aprobado; los campos de presentacion (nombre del producto) nunca se hashean.
/// </summary>
public static class AuthorizationContextCanonicalizer
{
    public const int MaxProductNameLength = 200;

    // T3b1 (F2b, S4): el nombre del cliente tambien es presentacion en el contexto de sobregiro.
    public const int MaxCustomerNameLength = 200;

    private const string DecimalFormat = "0.############################";

    public static AuthorizationContextPayload BuildManualPriceOverridePayload(ManualPriceOverrideContext context, string? productName)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AuthorizationContextPayload(
            ComputeManualPriceOverrideHash(context),
            BuildDisplayJson(context, productName));
    }

    public static string ComputeManualPriceOverrideHash(ManualPriceOverrideContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var canonicalJson = string.Concat(
            "{\"productId\":", context.ProductId.ToString(CultureInfo.InvariantCulture),
            ",\"quantity\":", FormatDecimal(context.Quantity),
            ",\"customUnitPriceUsd\":", FormatNullableDecimal(context.CustomUnitPriceUsd),
            ",\"customUnitPriceLocal\":", FormatNullableDecimal(context.CustomUnitPriceLocal),
            "}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    /// <summary>
    /// T3b1 (F2b, S4): contexto del sobregiro de cupo. El payload de presentacion lleva el nombre
    /// del cliente para que el supervisor vea a quien autoriza; el hash liga cliente, venta, monto
    /// financiado y credito disponible exactos (el nombre jamas se hashea, mismo criterio que el
    /// nombre de producto del override manual).
    /// </summary>
    public static AuthorizationContextPayload BuildCreditOverLimitPayload(CreditOverLimitContext context, string? customerName)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new AuthorizationContextPayload(
            ComputeCreditOverLimitHash(context),
            BuildCreditOverLimitDisplayJson(context, customerName));
    }

    public static string ComputeCreditOverLimitHash(CreditOverLimitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var canonicalJson = string.Concat(
            "{\"customerId\":", context.CustomerId.ToString(CultureInfo.InvariantCulture),
            ",\"saleId\":", context.SaleId.ToString(CultureInfo.InvariantCulture),
            ",\"financedUsd\":", FormatDecimal(context.FinancedUSD),
            ",\"availableCreditUsd\":", FormatDecimal(context.AvailableCreditUSD),
            "}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static string BuildDisplayJson(ManualPriceOverrideContext context, string? productName)
    {
        var boundedName = productName is { Length: > MaxProductNameLength }
            ? productName[..MaxProductNameLength]
            : productName;

        return JsonSerializer.Serialize(new
        {
            productId = context.ProductId,
            productName = boundedName,
            quantity = context.Quantity,
            customUnitPriceUsd = context.CustomUnitPriceUsd,
            customUnitPriceLocal = context.CustomUnitPriceLocal
        });
    }

    private static string BuildCreditOverLimitDisplayJson(CreditOverLimitContext context, string? customerName)
    {
        var boundedName = customerName is { Length: > MaxCustomerNameLength }
            ? customerName[..MaxCustomerNameLength]
            : customerName;

        return JsonSerializer.Serialize(new
        {
            customerId = context.CustomerId,
            customerName = boundedName,
            saleId = context.SaleId,
            financedUSD = context.FinancedUSD,
            availableCreditUSD = context.AvailableCreditUSD
        });
    }

    private static string FormatDecimal(decimal value)
        => value.ToString(DecimalFormat, CultureInfo.InvariantCulture);

    private static string FormatNullableDecimal(decimal? value)
        => value.HasValue ? FormatDecimal(value.Value) : "null";
}

public sealed record ManualPriceOverrideContext
{
    public int ProductId { get; init; }

    public decimal Quantity { get; init; }

    public decimal? CustomUnitPriceUsd { get; init; }

    public decimal? CustomUnitPriceLocal { get; init; }
}

/// <summary>
/// T3b1 (F2b, S4): contexto canonico de la autorizacion de sobregiro CreditOverLimit. Se calcula
/// igual al crear la solicitud (hub/REST) y al consumir el token (cierre a credito), de modo que
/// el token quede ligado al cliente, la venta, el monto financiado y el credito disponible que el
/// supervisor realmente aprobo.
/// </summary>
public sealed record CreditOverLimitContext
{
    public int CustomerId { get; init; }

    public int SaleId { get; init; }

    public decimal FinancedUSD { get; init; }

    public decimal AvailableCreditUSD { get; init; }
}

public sealed record AuthorizationContextPayload(string ContextHash, string ContextJson);
