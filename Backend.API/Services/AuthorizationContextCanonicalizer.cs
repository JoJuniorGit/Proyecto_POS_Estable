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

public sealed record AuthorizationContextPayload(string ContextHash, string ContextJson);
