using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Core.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class SalesService
{
    /// <summary>
    /// 8.211-T3a (S6): perfil monetario congelado en la venta. Es aditivo: no participa del
    /// cálculo de montos, tasas ni redondeos, por lo que un fallo de lectura nunca aborta el cobro.
    /// </summary>
    private record MonetaryProfileSnapshot(string PrimaryCurrencyCode, string? SecondaryCurrencyCode, string MonetaryMode)
    {
        public static readonly MonetaryProfileSnapshot Default = new("USD", "VES", "Dual");
    }

    // 8.211-T3c3c2 (S7): el cierre regular admite Dual USD/{VES,COP,MXN,EUR} (secundaria 2dp)
    // y Single con una moneda primaria de 2 decimales. Hold, abonos y adelantos mantienen
    // allowSingle=false y rechazan Single antes de cualquier mutación con el mensaje de
    // operación pendiente.
    private const string SupportedCheckoutProfileMessage =
        "El perfil monetario actual no está habilitado para ventas: esta fase admite Dual USD/VES, USD/COP, USD/MXN o USD/EUR, o Single con una moneda de 2 decimales.";

    private const string SingleOperationPendingMessage =
        "La operación con moneda única se habilitará en la siguiente fase.";

    // 8.211-T3c3c2 (S7): secundarias 2dp habilitadas para el checkout bimonetario. VES conserva
    // la regla cash 2A; COP/MXN/EUR usan su unidad menor (2 decimales).
    private static readonly HashSet<string> SupportedDualSecondaryCurrencies = new(StringComparer.Ordinal)
    {
        "VES",
        "COP",
        "MXN",
        "EUR"
    };

    private static void EnsureSupportedCheckoutMonetaryProfile(MonetaryProfileSnapshot profile, bool allowSingle)
    {
        if (string.Equals(profile.MonetaryMode, "Dual", StringComparison.Ordinal)
            && string.Equals(CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode), "USD", StringComparison.Ordinal))
        {
            var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
            if (secondary is not null && SupportedDualSecondaryCurrencies.Contains(secondary))
            {
                return;
            }
        }

        if (string.Equals(profile.MonetaryMode, "Single", StringComparison.Ordinal)
            && profile.SecondaryCurrencyCode is null)
        {
            if (!allowSingle)
            {
                throw new InvalidOperationException(SingleOperationPendingMessage);
            }

            // Guard against malformed profiles before the catalog throws its own argument error:
            // an unsupported primary code must reject with the profile contract message.
            if (CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode) is not null)
            {
                var precision = CurrencyPrecisionCatalog.For(profile.PrimaryCurrencyCode);
                if (precision.DigitalDecimals == 2 && precision.CashDecimals == 2)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException(SupportedCheckoutProfileMessage);
    }

    /// <summary>
    /// 8.211-T3c3b2 (S7/S8): every payment method must belong to the active profile. The method
    /// currency is the persisted one (name is only the legacy fallback) canonicalized to ISO.
    /// </summary>
    private static void EnsurePaymentMethodCurrencyMatchesProfile(MonetaryProfileSnapshot profile, PaymentMethod method)
    {
        string? methodCurrency = CurrencyCodeNormalizer.NormalizeToIso(ResolvePaymentMethodCurrency(method));
        bool isDual = string.Equals(profile.MonetaryMode, "Dual", StringComparison.Ordinal);
        bool isAllowed = string.Equals(methodCurrency, profile.PrimaryCurrencyCode, StringComparison.Ordinal)
            || (isDual && string.Equals(methodCurrency, profile.SecondaryCurrencyCode, StringComparison.Ordinal));

        if (!isAllowed)
        {
            throw new ArgumentException(
                $"El método de pago '{method.Name}' usa la moneda {methodCurrency}, pero el perfil actual solo admite {profile.PrimaryCurrencyCode}{(isDual ? $" y {profile.SecondaryCurrencyCode}" : string.Empty)}.");
        }
    }

    /// <summary>
    /// 8.211-T3c3c1 (S4/S5): local cash unit. Single always uses the primary currency; Dual uses
    /// the normalized secondary (VES is presented as Bs.S). The legacy default (Dual USD/VES)
    /// therefore keeps the historical integer cash rule.
    /// </summary>
    private static bool AllowsFractionalLocalCash(MonetaryProfileSnapshot profile)
    {
        if (string.Equals(profile.MonetaryMode, "Single", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(profile.MonetaryMode, "Dual", StringComparison.Ordinal))
        {
            return false;
        }

        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        return secondary is not null
            && !string.Equals(secondary, "VES", StringComparison.Ordinal);
    }

    /// <summary>
    /// 8.211-T3c3c1 (S4): currency of the physical local drawer. Single uses the primary; Dual
    /// uses the normalized secondary (VES displays as Bs.S, other ISO codes as-is).
    /// </summary>
    private static string ResolveLocalDrawerCurrency(MonetaryProfileSnapshot profile)
    {
        if (string.Equals(profile.MonetaryMode, "Single", StringComparison.Ordinal))
        {
            return profile.PrimaryCurrencyCode;
        }

        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        return secondary is null || string.Equals(secondary, "VES", StringComparison.Ordinal)
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : secondary;
    }

    /// <summary>
    /// 8.211-T3c3d (S4/S5): true only for the Dual USD + non-VES pair, the only regime whose
    /// rate is a manually loaded 8dp quote. BCV (USD/VES), Single and malformed profiles are not
    /// manual pairs.
    /// </summary>
    private static bool UsesManualPairRate(MonetaryProfileSnapshot profile)
    {
        if (!string.Equals(profile.MonetaryMode, "Dual", StringComparison.Ordinal))
        {
            return false;
        }

        var primary = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode);
        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        return string.Equals(primary, "USD", StringComparison.Ordinal)
            && secondary is not null
            && !string.Equals(secondary, "VES", StringComparison.Ordinal);
    }

    /// <summary>
    /// 8.211-T3c3d (S4/S5): rate frozen in the persisted columns (18,4). A manual pair keeps the
    /// exact 4dp snapshot; BCV keeps its legacy 2dp ceiling; Single is identity. The calculation
    /// rate (8dp for a manual pair) is passed separately to <see cref="RecalculateTotalAsync"/>.
    /// </summary>
    private static decimal RoundSnapshotRate(decimal rate, MonetaryProfileSnapshot profile)
    {
        if (string.Equals(profile.MonetaryMode, "Single", StringComparison.Ordinal))
        {
            return 1m;
        }

        return UsesManualPairRate(profile)
            ? PricingCalculator.RoundExchangeRate(rate, PricingCalculator.RateSnapshotDecimals)
            : PricingCalculator.RoundExchangeRateCeiling(rate);
    }

    /// <summary>
    /// 8.211-T3c3d (S4/S5): calculation rate paired with <see cref="RoundSnapshotRate"/>: the
    /// manual pair keeps its 8dp quote; BCV/Single keep using the persisted snapshot (null).
    /// </summary>
    private static decimal? ResolveCalculationRate(decimal rate, MonetaryProfileSnapshot profile)
        => UsesManualPairRate(profile)
            ? PricingCalculator.RoundExchangeRate(rate, PricingCalculator.ManualRateDecimals)
            : null;

    /// <summary>
    /// 8.211-T6-U4: an edit request may omit ExchangeRate (Web does). For a manual pair the
    /// calculation still needs the 8dp quote; resolve it from the daily manual rate instead of
    /// degrading to the persisted 4dp AppliedRate. Missing service/quote fails closed.
    /// </summary>
    private async Task<decimal> ResolveManualPairCalculationRateAsync(
        MonetaryProfileSnapshot profile,
        CancellationToken cancellationToken)
    {
        if (_currencyRateService is null)
        {
            throw new InvalidOperationException(
                "No hay servicio de tasas manuales configurado para el par monetario activo.");
        }

        var primary = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode) ?? "USD";
        var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
        if (secondary is null)
        {
            throw new InvalidOperationException(
                "La moneda secundaria del par manual no es válida.");
        }

        var quote = await _currencyRateService.GetRateAsync(primary, secondary, null, cancellationToken);
        decimal rate = PricingCalculator.RoundExchangeRate(quote.Rate, PricingCalculator.ManualRateDecimals);
        if (rate <= 0m)
        {
            throw new InvalidOperationException(
                $"La tasa manual configurada para el par {primary}/{secondary} no es válida.");
        }

        return rate;
    }

    /// <summary>
    /// 8.211-T3c3c1 (S4): frozen sale rate provenance. Identity in Single, BCV for USD/VES and
    /// the manual daily pair for any other Dual secondary.
    /// </summary>
    private static string ResolveAppliedRateSource(MonetaryProfileSnapshot profile)
    {
        if (string.Equals(profile.MonetaryMode, "Single", StringComparison.Ordinal))
        {
            return "Identity";
        }

        return UsesManualPairRate(profile) ? "Manual" : "Bcv";
    }

    /// <summary>
    /// Paso 14 (S4/S9): materializes the cost snapshot in the transaction currency at completion.
    /// Uses only the immutable sale snapshot (<see cref="Sale.AppliedRate"/>) and the line's
    /// <see cref="SaleItem.UnitCostUSD"/>: in Dual it converts USD to the secondary/local currency,
    /// in Single it converts USD to the primary currency (identity). Never reads the current
    /// product cost or an external rate; unknown cost stays null.
    /// </summary>
    private static void ApplyLocalCostSnapshots(Sale sale)
    {
        if (sale.AppliedRate <= 0m)
        {
            return;
        }

        foreach (var item in sale.Items)
        {
            item.UnitCostLocal = item.UnitCostUSD.HasValue
                ? Math.Round(item.UnitCostUSD.Value * sale.AppliedRate, 4, MidpointRounding.AwayFromZero)
                : null;
        }
    }

    /// <summary>
    /// Paso 14 (S7): resolves the immutable fee snapshot for a payment about to be persisted.
    /// A missing payment-method row is a legacy empty catalog (the flow already tolerates it and
    /// no rule can reference the method): the snapshot stays <see cref="PaymentMethodFeeSnapshot.None"/>.
    /// When the row exists the resolver enforces cash and configuration rules.
    /// </summary>
    private async Task<PaymentMethodFeeSnapshot> ResolvePaymentMethodFeeAsync(
        PaymentMethod? knownPaymentMethod,
        int paymentMethodId,
        DateTime paymentDateUtc,
        decimal amountUsd,
        decimal amountLocal,
        decimal rate,
        string localCurrencyCode,
        CancellationToken cancellationToken)
    {
        if (knownPaymentMethod is null)
        {
            return PaymentMethodFeeSnapshot.None;
        }

        return await PaymentMethodFeeResolver.ResolveAsync(
            _context,
            paymentMethodId,
            paymentDateUtc,
            amountUsd,
            amountLocal,
            rate,
            localCurrencyCode,
            cancellationToken);
    }

    /// <summary>
    /// Paso 14 (S7): local currency of the frozen monetary regime, used by the fee resolver.
    /// </summary>
    private static string ResolveLocalCurrencyCode(MonetaryProfileSnapshot profile)
        => PaymentMethodFeeResolver.ResolveLocalCurrencyCode(
            profile.MonetaryMode,
            profile.PrimaryCurrencyCode,
            profile.SecondaryCurrencyCode);

    /// <summary>
    /// 8.211-T4a (S10/F4a): perfil para etiquetar el recibo impreso tras el commit. Cualquier
    /// fallo degrada al default del contexto (códigos snapshot + símbolos ASCII por código) sin
    /// afectar la venta; por eso se lee con un token no cancelable después de confirmar.
    /// </summary>
    private async Task<Core.DTOs.MonetaryProfileDto?> TryReadReceiptMonetaryProfileAsync()
    {
        if (_monetaryProfileService is null)
        {
            return null;
        }

        try
        {
            return await _monetaryProfileService.GetAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // La venta ya está confirmada; el recibo cae a las etiquetas por código persistido.
            // Se registra en Debug para no duplicar la alerta operativa del snapshot de la venta.
            _logger?.LogDebug(ex, "[SalesService] No se pudo leer el perfil monetario para el recibo impreso; se usan las etiquetas por código.");
            return null;
        }
    }

    private async Task<MonetaryProfileSnapshot> ResolveMonetaryProfileSnapshotAsync(
        CancellationToken cancellationToken,
        bool failClosed = true)
    {
        if (_monetaryProfileService is null)
        {
            return MonetaryProfileSnapshot.Default;
        }

        try
        {
            var profile = await _monetaryProfileService.GetAsync(cancellationToken);

            var primary = string.IsNullOrWhiteSpace(profile.PrimaryCurrencyCode)
                ? "USD"
                : profile.PrimaryCurrencyCode;

            // Single mode persists NULL: the secondary currency does not exist in that regime.
            var secondary = profile.Mode == MonetaryMode.Dual
                ? profile.SecondaryCurrencyCode ?? "VES"
                : null;

            return new MonetaryProfileSnapshot(primary, secondary, profile.Mode.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The snapshot describes the regime; it must never fail the sale or change amounts.
            // Financial mutations that route physical cash can opt into fail-closed instead, so a
            // non-VES profile is never silently treated as Dual USD/VES.
            if (failClosed)
            {
                throw new InvalidOperationException(
                    "No se pudo leer el perfil monetario para enrutar el efectivo. Verifique la configuración monetaria.",
                    ex);
            }

            _logger?.LogWarning(ex, "[SalesService] No se pudo leer el perfil monetario; se usa el snapshot por defecto USD/VES/Dual.");
            return MonetaryProfileSnapshot.Default;
        }
    }

    public async Task<int> CompleteSaleAsync(
        int saleId, 
        decimal exchangeRate, 
        IEnumerable<PaymentInfo> payments, 
        decimal roundingAdjustment = 0, 
        int? cashierId = null, 
        bool isPendingPickup = false, 
        string? idempotencyKey = null,
        byte[]? idempotencyPayloadHash = null,
        System.Threading.CancellationToken cancellationToken = default,
        int? actingUserId = null)
    {
        // 8.187-T2 (F2a): el cierre regular delega en el núcleo compartido SIN financiamiento.
        // Comportamiento observable intacto (mismo camino, mismos gates y mismos mensajes).
        return await CompleteSaleCoreAsync(
            saleId,
            exchangeRate,
            payments,
            roundingAdjustment,
            cashierId,
            isPendingPickup,
            idempotencyKey,
            idempotencyPayloadHash,
            cancellationToken,
            actingUserId,
            allowFinancedRemainder: false,
            idempotentRequestPath: null,
            creditOverLimitAuthorized: false);
    }

    /// <summary>
    /// 8.187-T2 (F2a, S2/S6): núcleo transaccional compartido del cierre de venta. Con
    /// <paramref name="allowFinancedRemainder"/> en false es el cierre regular histórico; con
    /// true (checkout a crédito) el remanente efectivo en Bs.S por encima del polvo decimal se
    /// financia como Cuenta por Cobrar cuando el cliente es elegible. Lo cobrado genera pagos y
    /// movimientos de caja exactamente igual que en la ruta regular; lo financiado no toca caja.
    /// </summary>
    private async Task<int> CompleteSaleCoreAsync(
        int saleId, 
        decimal exchangeRate, 
        IEnumerable<PaymentInfo> payments, 
        decimal roundingAdjustment, 
        int? cashierId, 
        bool isPendingPickup, 
        string? idempotencyKey,
        byte[]? idempotencyPayloadHash,
        System.Threading.CancellationToken cancellationToken,
        int? actingUserId,
        bool allowFinancedRemainder,
        string? idempotentRequestPath,
        // T3b1 (S4): exclusivo del cierre a crédito; false preserva byte a byte la ruta regular y
        // la primera pasada del cierre a crédito (que debe señalar el sobregiro sin omitirlo).
        bool creditOverLimitAuthorized = false)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger?.LogInformation("[TX_START] CorrelationId={CorrelationId}, SaleId={SaleId}, IsolationLevel=ReadCommitted", correlationId, saleId);

        int checkoutRetryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkoutRetryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }
            var sale = await GetSaleEntityAsync(saleId);

            // Idempotency check: if sale is already completed, return existing InvoiceNumber immediately
            if (sale.Status == SaleStatus.Completed && sale.InvoiceNumber.HasValue)
            {
                _logger?.LogInformation("[SalesService] Idempotency: Venta #{SaleId} ya se encontraba completada con Factura N° {InvoiceNumber}. Retornando consecutivo.", saleId, sale.InvoiceNumber.Value);
                return sale.InvoiceNumber.Value;
            }

            EnsureHoldClaimAccess(sale, actingUserId);

            // 8.211-T3a (S6): se resuelve UNA vez por intento, fuera de la transacción SQL.
            var monetaryProfile = await ResolveMonetaryProfileSnapshotAsync(cancellationToken);
            // 8.211-T3c3b2 (S4/S5): el cierre regular (y el de crédito) habilita Single 2dp.
            EnsureSupportedCheckoutMonetaryProfile(monetaryProfile, allowSingle: true);

            // 8.187-T2 (F2a, S2): la modalidad a crédito exige vincular un cliente registrado
            // ANTES de evaluar solvencia y constituir la obligación: sin cliente no hay CxC.
            if (allowFinancedRemainder && !sale.CustomerId.HasValue)
            {
                throw new ArgumentException("Para registrar una venta a crédito se requiere seleccionar o crear un cliente registrado (Nombre, Cédula y Teléfono).");
            }

            if (exchangeRate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            // 8.5-A5/8.6-B3 / PERF-05: Anclaje de tasa BCV del día vs. tasa recibida del cliente.
            // Se resuelve ANTES de abrir la transacción SQL y los locks para minimizar la contención transaccional.
            exchangeRate = await ResolveAnchoredRateAsync(
                exchangeRate,
                contextLabel: "CompleteSale",
                referenceId: saleId);

            // 8.211-T3c3d (S4/S5): persisted snapshot (4dp manual / 2dp BCV); the totals are
            // recalculated with the calculation rate below, never with this snapshot.
            decimal roundedAppliedRate = RoundSnapshotRate(exchangeRate, monetaryProfile);

            // PERF-06: Pre-validación de métodos de pago y carga de catálogo fuera de la transacción
            // y del advisory lock para reducir la ventana de contención sobre tablas de ventas/caja.
            var paymentMethodsDict = new Dictionary<int, PaymentMethod>();
            bool allowsFractionalCash = AllowsFractionalLocalCash(monetaryProfile);
            if (payments != null && payments.Any())
            {
                var paymentMethodIds = payments.Select(p => p.PaymentMethodId).Distinct().ToList();
                paymentMethodsDict = await _context.PaymentMethods
                    .AsNoTracking()
                    .Where(pm => paymentMethodIds.Contains(pm.Id))
                    .ToDictionaryAsync(pm => pm.Id, cancellationToken);

                foreach (var p in payments)
                {
                    // 8.7-B2: Rechazo de montos NEGATIVOS por método, validado sobre los valores crudos
                    // antes de resolver la conversión (un negativo no puede quedar enmascarado por el par).
                    // Los ceros absolutos se purgan en la sanitización pre-persistencia y los montos mixtos
                    // (una sola moneda) se resuelven en el par USD/Bs.S consistente.
                    if (p.Amount < 0m || p.AmountLocal < 0m)
                    {
                        throw new ArgumentException($"La validación del método de pago (PaymentMethodId={p.PaymentMethodId}) rechaza montos negativos. Monto USD={p.Amount}, Monto Bs.S={p.AmountLocal}.");
                    }

                    var (_, amountLocal) = ResolveConsistentPaymentAmounts(p.Amount, p.AmountLocal, exchangeRate);

                    paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var paymentMethod);

                    // 8.6-B3: Validación pre-persistencia del método de pago: un PaymentMethodId inexistente
                    // o inactivo aborta el cobro (evita asociar pagos a configuraciones inválidas).
                    if (paymentMethod == null || !paymentMethod.IsActive)
                    {
                        throw new ArgumentException($"Método de pago inválido o inactivo: PaymentMethodId={p.PaymentMethodId}. Verifique la configuración de métodos de pago.");
                    }

                    // 8.211-T3c3b2 (S7/S8): el método debe pertenecer al perfil monetario activo.
                    EnsurePaymentMethodCurrencyMatchesProfile(monetaryProfile, paymentMethod);

                    // Validación de integridad: en Dual el efectivo solo acepta montos enteros
                    // (política histórica Bs.S). Single usa la unidad menor de su moneda primaria.
                    if (paymentMethod.IsCash && !allowsFractionalCash && amountLocal % 1 != 0)
                    {
                        throw new ArgumentException("El método de pago en efectivo solo acepta montos enteros.");
                    }
                }
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            if (transaction != null && _inventoryService != null)
            {
                var rawDbTx = transaction.GetDbTransaction();
                await _inventoryService.EnrollInTransactionAsync(rawDbTx, cancellationToken);
            }

            try
            {
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", new object[] { 2, saleId }, cancellationToken);
                }

                // 8.187-T2 (F2a, S2/S4): sección crítica POR CLIENTE del cierre a crédito. El lock
                // por venta no impide que dos ventas concurrentes del mismo cliente crucen el cupo;
                // este advisory lock transaccional serializa la evaluación de solvencia y el alta de
                // la obligación (misma técnica del lock por venta; clase 3 = cartera CxC).
                if (allowFinancedRemainder && _context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", new object[] { 3, sale.CustomerId!.Value }, cancellationToken);
                }

                if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
                    throw new InvalidOperationException("La venta no se encuentra en estado Pendiente o En Espera.");

                if (isPendingPickup)
                {
                    if (!sale.CustomerId.HasValue)
                    {
                        throw new ArgumentException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                    }

                    var cust = await _context.Customers.FindAsync(new object[] { sale.CustomerId.Value }, cancellationToken);
                    if (cust == null || cust.IsDefault || cust.CedulaOrRif == "V-00000000" || cust.Name.StartsWith("Consumidor Final", StringComparison.OrdinalIgnoreCase) || cust.Name.StartsWith("Cliente General", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException("Para registrar un apartado pagado (mercancía en custodia), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                    }
                }

                if (cashierId.HasValue)
                {
                    sale.CashierId = cashierId.Value;
                }

                sale.AppliedRate = roundedAppliedRate;

                // 8.211-T3a (S6): snapshot aditivo del perfil vigente, sin tocar totales ni tasa.
                sale.PrimaryCurrencyCode = monetaryProfile.PrimaryCurrencyCode;
                sale.SecondaryCurrencyCode = monetaryProfile.SecondaryCurrencyCode;
                sale.MonetaryMode = monetaryProfile.MonetaryMode;

                await RecalculateTotalAsync(sale, cancellationToken: cancellationToken, calculationRate: exchangeRate);

            decimal existingPaidUsd = sale.Payments.Sum(p => p.Amount);
            decimal newPaymentsPaidUsd = payments != null 
                ? payments.Sum(p => p.Amount > 0 ? p.Amount : (p.AmountLocal > 0 && exchangeRate > 0 ? Math.Round(p.AmountLocal / exchangeRate, 2, MidpointRounding.AwayFromZero) : 0m)) 
                : 0m;
            decimal totalPaidUsd = Math.Round(existingPaidUsd + newPaymentsPaidUsd, 2, MidpointRounding.AwayFromZero);
            decimal remainingBalanceUsd = Math.Round(sale.TotalUSD - totalPaidUsd, 2, MidpointRounding.AwayFromZero);

            // 8.185-T3 (S4/S5): remanente ESPEJO en el dominio Bs.S contra el TotalBsS EFECTIVO
            // (derivado por línea en RecalculateTotalAsync). Los pagos aportan su monto local y los
            // solo-USD se convierten por la tasa anclada; la venta se rechaza solo si falta cobertura
            // en AMBOS dominios (0.05 USD por dominio), de modo que un pago exacto del total
            // efectivo —aunque difiera del canónico— liquida la venta sin falsos descuadres.
            decimal existingPaidBsS = sale.Payments.Sum(p => p.AmountBsS > 0m ? p.AmountBsS : PricingCalculator.ToBsS(p.Amount, exchangeRate));
            decimal newPaymentsPaidBsS = payments != null
                ? payments.Sum(p => p.AmountLocal > 0m ? p.AmountLocal : PricingCalculator.ToBsS(p.Amount, exchangeRate))
                : 0m;
            decimal totalPaidBsSDomain = PricingCalculator.RoundToDigital(existingPaidBsS + newPaymentsPaidBsS);
            decimal remainingBalanceBsS = PricingCalculator.RoundToDigital(sale.TotalBsS - totalPaidBsSDomain);
            bool isUnderpaidInBothDomains = remainingBalanceUsd > 0.05m && remainingBalanceBsS > 0.05m * exchangeRate;

            if (isPendingPickup)
            {
                if (isUnderpaidInBothDomains)
                {
                    throw new ArgumentException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), la venta debe estar pagada al 100% (saldo restante $0.00).");
                }

                bool isDefaultCust = sale.CustomerId == null || sale.Customer == null || sale.Customer.IsDefault || (sale.CustomerName != null && sale.CustomerName.ToLower().Contains("consumidor final"));
                if (isDefaultCust)
                {
                    throw new ArgumentException("Para registrar un apartado en custodia (Mercancía Pendiente por Retirar), se requiere seleccionar o crear un cliente real (Nombre, Cédula y Teléfono).");
                }
            }

            var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(exchangeRate, cancellationToken);

            // 8.202-H1 (S2): orden canónico venta (clase 2) / cliente (clase 3) -> sesión. Solo se
            // serializa el cierre cuando va a escribir en la gaveta (cobro cash o vuelto físico);
            // el re-check bajo el lock aborta si la sesión se cerró mientras esperábamos.
            bool requiresCashDrawerWrite = remainingBalanceBsS < -0.05m * exchangeRate;
            if (!requiresCashDrawerWrite && payments != null)
            {
                foreach (var payment in payments)
                {
                    if (payment.Amount > 0m
                        && paymentMethodsDict.TryGetValue(payment.PaymentMethodId, out var cashMethod)
                        && cashMethod.IsCash)
                    {
                        requiresCashDrawerWrite = true;
                        break;
                    }
                }
            }

            if (requiresCashDrawerWrite)
            {
                await _cashDrawerService.LockOpenSessionForWriteAsync(activeSession.Id, cancellationToken);
            }

            if (!sale.InvoiceNumber.HasValue)
            {
                sale.InvoiceNumber = await GenerateNextInvoiceNumberAsync();
            }

            if (payments != null && payments.Any())
            {
                string localCurrencyCode = ResolveLocalCurrencyCode(monetaryProfile);
                foreach (var p in payments)
                {
                    var (amountUsd, amountLocal) = ResolveConsistentPaymentAmounts(p.Amount, p.AmountLocal, exchangeRate);

                    paymentMethodsDict.TryGetValue(p.PaymentMethodId, out var paymentMethod);

                    _logger?.LogDebug("[CURRENCY CONVERSION DEBUG] Método: {Method}, Monto Bs.S: {BsS}, Tasa AppliedRate: {Rate}, Monto USD Calculado: {Usd}", p.PaymentMethodId, amountLocal, exchangeRate, amountUsd);

                    var currencyCode = ResolvePaymentMethodCurrency(paymentMethod);
                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale.Id,
                        PaymentMethodId = p.PaymentMethodId,
                        Amount = Math.Round(amountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(amountLocal, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = exchangeRate,
                        ReferenceNumber = p.Reference,
                        CreatedAt = DateTime.UtcNow,
                        CurrencyCode = currencyCode,
                        OriginalAmount = ResolveOriginalAmount(amountUsd, amountLocal, currencyCode)
                    };

                    // Paso 14 (S7): la comision es un costo del negocio; se congela aqui y no
                    // participa de los totales, la tasa ni el vuelto que se cobra al cliente.
                    var feeSnapshot = await ResolvePaymentMethodFeeAsync(
                        paymentMethod,
                        p.PaymentMethodId,
                        paymentEntity.CreatedAt,
                        amountUsd,
                        amountLocal,
                        exchangeRate,
                        localCurrencyCode,
                        cancellationToken);
                    paymentEntity.ApplyFeeSnapshot(feeSnapshot);

                    sale.Payments.Add(paymentEntity);

                    if (amountUsd > 0 && paymentMethod != null && paymentMethod.IsCash)
                    {
                        var cashTx = new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = amountUsd,
                            ExchangeRate = exchangeRate,
                            AmountLocal = amountLocal,
                            IsPhysicalCash = true,
                            Description = $"Factura N° {sale.InvoiceNumber}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = p.PaymentMethodId,
                            // 8.211-T3b3 (S6): la moneda del ingreso fisico acompania al metodo.
                            CurrencyCode = currencyCode
                        };
                        _context.CashTransactions.Add(cashTx);
                    }
                }
            }

            // H-SAL-3: Registro de vuelto como egreso (Expense) y validación de límites de sobrepago
            // 8.6-C1: El vuelto pasa por CashDrawerService.RecordSaleChangeAsync, que aplica advisory lock
            // y verifica que la caja tenga saldo para cubrirlo (evita saldo negativo).
            // 8.185-T3 (decisión mantenedor): el vuelto es DUAL — el exceso se mide contra el total
            // EFECTIVO Bs.S (pagos USD acreditados a la tasa anclada), no contra el canónico. Pagar
            // exacto el efectivo publicado no devuelve el escalón de la política (queda en caja) y un
            // pago por encima devuelve SOLO el exceso contra el efectivo. Con ExactMath (efectivo ==
            // canónico) el resultado coincide con el contrato previo (tolerancia 0.05 USD escalada).
            if (remainingBalanceBsS < -0.05m * exchangeRate)
            {
                decimal changeBsS = Math.Abs(remainingBalanceBsS);
                decimal changeUsd = PricingCalculator.ToUSD(changeBsS, exchangeRate);
                if (changeUsd > 100m && changeUsd > sale.TotalUSD)
                {
                    throw new ArgumentException($"El sobrepago o vuelto requerido (${changeUsd:F2} USD) excede los límites operacionales de seguridad.");
                }

                // El vuelto se entrega en la moneda local de la gaveta: Single usa la primaria y
                // Dual la secundaria normalizada (VES se presenta como Bs.S). Se atribuye al
                // efectivo de esa moneda, nunca al primer efectivo del pago original (puede ser USD).
                string localDrawerCurrency = ResolveLocalDrawerCurrency(monetaryProfile);

                int? cashMethodId = null;
                foreach (var paymentMethodId in sale.Payments.Select(p => p.PaymentMethodId).Distinct())
                {
                    if (paymentMethodsDict.TryGetValue(paymentMethodId, out var cashMethod)
                        && cashMethod.IsCash
                        && string.Equals(
                            PaymentMethodCurrencyResolver.ResolveStoredOrName(cashMethod.Currency, cashMethod.Name),
                            localDrawerCurrency,
                            StringComparison.Ordinal))
                    {
                        cashMethodId = paymentMethodId;
                        break;
                    }
                }

                // Ingresos cash de la venta aún en el tracker (persistidos junto con todo el cobro): se informan
                // al chequeo de saldo para que el vuelto NO se rechace por no verlos aún en la BD.
                // Solo el efectivo de la moneda local de la gaveta financia un vuelto en esa moneda.
                decimal pendingCashIncomeBsS = _context.CashTransactions.Local
                    .Where(t => t.SessionId == activeSession.Id
                             && t.Type == CashTransactionType.Income
                             && t.Source == CashTransactionSource.SalePayment
                             && t.IsPhysicalCash
                             && t.PaymentMethodId.HasValue
                             && paymentMethodsDict.TryGetValue(t.PaymentMethodId.Value, out var localCashMethod)
                             && localCashMethod.IsCash
                             && string.Equals(
                                 PaymentMethodCurrencyResolver.ResolveStoredOrName(localCashMethod.Currency, localCashMethod.Name),
                                 localDrawerCurrency,
                                 StringComparison.Ordinal))
                    .Sum(t => t.AmountLocal);

                await _cashDrawerService.RecordSaleChangeAsync(
                    sessionId: activeSession.Id,
                    changeUsd: changeUsd,
                    changeBsS: changeBsS,
                    exchangeRate: exchangeRate,
                    description: $"Vuelto Factura N° {sale.InvoiceNumber}",
                    saleId: sale.Id,
                    cashPaymentMethodId: cashMethodId,
                    pendingCashIncomeBsS: pendingCashIncomeBsS,
                    cancellationToken: cancellationToken);
                _logger?.LogInformation("[SalesService] Vuelto registrado en caja: ${ChangeUsd} USD / Bs. {ChangeBsS} para Factura N° {InvoiceNumber}",
                    changeUsd, changeBsS, sale.InvoiceNumber);
            }

            // 8.187-T2 (F2a, S2/S6): remanente financiado del cierre a crédito derivado del TotalBsS
            // EFECTIVO (política cash incluida) contra lo efectivamente cobrado en el dominio Bs.S.
            // Con allowFinancedRemainder=false el bloque es inerte y la ruta regular queda intacta.
            decimal financedBsS = 0m;
            decimal financedUsd = 0m;
            bool hasFinancedRemainder = false;
            if (allowFinancedRemainder)
            {
                financedBsS = PricingCalculator.RoundToDigital(sale.TotalBsS - totalPaidBsSDomain);
                // 8.211-T3c3d (S4/S5): the principal is derived from the calculation rate (8dp
                // manual), never from the 4dp AppliedRate snapshot; the snapshot stays historical.
                financedUsd = financedBsS > 0m
                    ? Math.Round(financedBsS / exchangeRate, 4, MidpointRounding.AwayFromZero)
                    : 0m;
                // Polvo decimal (S7): ≤ 0.005 USD delega al cierre regular (sin Cuenta por Cobrar).
                hasFinancedRemainder = financedUsd > 0.005m;
            }

            // 1. Defensive Aggregated Total Validation:
            if (sale.Payments.Sum(p => p.Amount) <= 0 && !sale.IsZeroAmountOrder && !hasFinancedRemainder)
            {
                throw new InvalidOperationException("Rechazo Defensivo: El total acumulado de los métodos de pago es <= 0. Se aborta el guardado local.");
            }

            if (sale.AppliedRate <= 0)
            {
                throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
            }

            // 2. Pre-Persistence Sanitation compatible with EF Core Change Tracker:
            var paymentsToRemove = sale.Payments.Where(p => p.Amount == 0).ToList();
            foreach (var payment in paymentsToRemove)
            {
                sale.Payments.Remove(payment);
            }

            if (isUnderpaidInBothDomains && !hasFinancedRemainder)
            {
                throw new ArgumentException("El monto ingresado no cubre la totalidad de la venta. El flujo de cobro requiere liquidación al 100%. Para abonos parciales o guardar pedidos en espera, utilice la opción 'Guardar en Espera'.");
            }

            // Es liquidación total
            sale.Status = SaleStatus.Completed;
            ClearHoldClaim(sale);
            sale.DeliveryStatus = isPendingPickup ? SaleDeliveryStatus.PendingPickup : SaleDeliveryStatus.Delivered;
            sale.Date = DateTime.UtcNow;
            sale.AppliedRate = RoundSnapshotRate(exchangeRate, monetaryProfile);
            await RecalculateTotalAsync(sale, cancellationToken: cancellationToken, calculationRate: exchangeRate);
            // 8.211-T3c2 (S6): snapshots aditivos de importes con los totales ya definitivos.
            // 8.211-T3c3b2 (S4): en Single no hay secundaria y la tasa efectiva es identidad.
            // 8.211-T3c3c1 (S4): la fuente es Bcv para USD/VES y Manual para el resto de Dual.
            // No recalculan nada: copian los totales canónicos de esta fase.
            sale.PrimaryAmount = sale.TotalUSD;
            sale.SecondaryAmount = monetaryProfile.MonetaryMode == "Dual" ? sale.TotalBsS : null;
            sale.AppliedRateSource = ResolveAppliedRateSource(monetaryProfile);
            sale.FinalPaidAmountBsS = sale.Payments.Sum(p => p.AmountBsS);

            // Paso 14 (S4/S9): costo local inmutable de cada línea con la tasa snapshot de la
            // venta. Solo se materializa al completar; las ediciones pendientes no lo escriben.
            ApplyLocalCostSnapshots(sale);

            // 8.149 (SEC-01): `roundingAdjustment` queda en la firma por compatibilidad del contrato (API/WPF),
            // pero se ignora: el ajuste se recalcula contra los pagos persistidos, espejo exacto del checkout-preview.
            decimal totalPaidBsS = sale.Payments.Sum(p => p.AmountBsS);
            decimal remainingUsd = Math.Round(sale.TotalUSD - sale.Payments.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            sale.RoundingAdjustment = remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS - sale.TotalBsS) : 0m;

            // 8.187-T2 (F2a, S2/S6): el remanente efectivo financiado se constituye como Cuenta por
            // Cobrar DESPUÉS de fijar los totales definitivos de la factura y ANTES de persistir:
            // la obligación y la venta entran en el mismo SaveChanges/transacción (factura al emitir).
            if (hasFinancedRemainder)
            {
                await CreateCreditReceivableAsync(sale, financedBsS, financedUsd, creditOverLimitAuthorized, cancellationToken);
            }

            // Synchronous Stock Deduction inside Transaction (H-SAL-2 / H-INV-1 / A1)
            var productsDict = new Dictionary<int, SaleProductInfoDto>();
            if (_inventoryService != null && sale.Items != null)
            {
                var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
                var fetched = await _inventoryService.GetSaleProductsByIdsAsync(productIds, cancellationToken);
                if (fetched != null)
                {
                    productsDict = fetched.ToDictionary(p => p.Id);
                }

                var stockDeductions = new List<StockDeductionRequest>();
                foreach (var item in sale.Items)
                {
                    if (productsDict.TryGetValue(item.ProductId, out var product) && product.IsCashAdvance)
                    {
                        continue;
                    }

                    stockDeductions.Add(new StockDeductionRequest(
                        item.ProductId,
                        -item.Quantity,
                        $"Sale #{sale.InvoiceNumber.Value}",
                        sale.Id));
                }

                if (stockDeductions.Count > 0)
                {
                    var allowNegativeStock = await IsAllowNegativeStockEnabledAsync();
                    await _inventoryService.UpdateStockBatchAsync(
                        stockDeductions,
                        userId: cashierId?.ToString(),
                        allowNegativeStock: allowNegativeStock,
                        cancellationToken: cancellationToken);
                }
            }

            // Transactional Outbox Message creation (A1 / H-SAL-2)
            var outboxPayload = JsonSerializer.Serialize(new
            {
                SaleId = sale.Id,
                InvoiceNumber = sale.InvoiceNumber.Value,
                Date = sale.Date,
                TotalUSD = sale.TotalUSD,
                TotalBsS = sale.TotalBsS,
                CashierId = sale.CashierId,
                IdempotencyKey = idempotencyKey,
                Items = sale.Items?.Select(i => new { i.ProductId, i.Quantity, i.UnitPrice, i.Subtotal }).ToList()
            });

            _context.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = "SaleCompleted",
                Payload = outboxPayload,
                CreatedAtUtc = DateTime.UtcNow,
                NextRetryUtc = DateTime.UtcNow,
                Status = "Pending",
                RetryCount = 0
            });

            // Persistencia de IdempotentRequest dentro de la transacción compartida si se proveyó clave y hash
            if (!string.IsNullOrWhiteSpace(idempotencyKey) && idempotencyPayloadHash != null)
            {
                _context.IdempotentRequests.Add(new IdempotentRequest
                {
                    Key = idempotencyKey,
                    // El cierre regular conserva su ruta histórica; el checkout a crédito (T3/F2b)
                    // puede pasar una ruta propia para que el replay idempotente no colisione.
                    RequestPath = idempotentRequestPath ?? $"/api/sales/{saleId}/complete",
                    PayloadHash = idempotencyPayloadHash,
                    StatusCode = 200,
                    ResponseBody = JsonSerializer.Serialize(sale.InvoiceNumber.Value),
                    UserId = actingUserId,
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddHours(24)
                });
            }

            _logger?.LogInformation(
                "[EF CORE ENTITY DEBUG] Persistiendo Sale ID: {SaleId}. Entidades SalePayment reales: {@Payments}",
                sale.Id,
                sale.Payments.Select(p => new { p.PaymentMethodId, p.Amount, p.AmountBsS }));

            await _context.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                }
            }

            await NotifyHoldOrdersChangedAsync();

            // 8.211-T4a (S10/F4a): el comprobante impreso usa los códigos snapshot de la venta y
            // el símbolo configurado del perfil. La lectura es best-effort: jamás falla una venta
            // ya confirmada por no poder leer settings.
            if (_receiptPrintQueue is not null)
            {
                var receiptProfile = await TryReadReceiptMonetaryProfileAsync();
                _receiptPrintQueue.Enqueue(Sales.Module.Receipts.SaleReceiptContext.CreateFrom(sale, receiptProfile));
            }

            _logger?.LogInformation("[TX_COMMIT] CorrelationId={CorrelationId}, SaleId={SaleId}, InvoiceNumber={InvoiceNumber}", correlationId, saleId, sale.InvoiceNumber.Value);

            try
            {
                var itemsSnapshot = (sale.Items ?? Enumerable.Empty<SaleItem>())
                    .Where(i => !productsDict.TryGetValue(i.ProductId, out var prod) || !prod.IsCashAdvance)
                    .Select(i => new SaleItemSnapshot(i.ProductId, i.Quantity))
                    .ToList();
                var saleMadeEvent = new SaleMadeEvent(sale.Id, sale.Date, itemsSnapshot, sale.InvoiceNumber.Value);
                await _mediator.Publish(saleMadeEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SalesService] Publicación de evento secundario SaleMadeEvent falló, pero la venta y el Outbox están garantizados en base de datos.");
            }

            return sale.InvoiceNumber.Value;
        }
        catch (OperationCanceledException opEx)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(System.Threading.CancellationToken.None);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(System.Threading.CancellationToken.None);
                }
            }
            _logger?.LogWarning(opEx, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason=OperationCanceled", correlationId, saleId);
            throw;
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(System.Threading.CancellationToken.None);
                if (_inventoryService != null)
                {
                    await _inventoryService.DetachFromTransactionAsync(System.Threading.CancellationToken.None);
                }
            }
            _logger?.LogError(ex, "[TX_ROLLBACK] CorrelationId={CorrelationId}, SaleId={SaleId}, Reason={Reason}", correlationId, saleId, ex.Message);
            throw;
        }
        });
    }

    private async Task PopulateItemsMetadataAsync(IEnumerable<Sale> sales)
    {
        if (_inventoryService == null || sales == null) return;

        var allItems = sales.Where(s => s.Items != null).SelectMany(s => s.Items).ToList();
        if (!allItems.Any()) return;

        var productIds = allItems.Select(i => i.ProductId).Distinct().ToList();
        var productsDict = new Dictionary<int, SaleProductInfoDto>();

        try
        {
            var fetched = await _inventoryService.GetSaleProductsByIdsAsync(productIds);
            if (fetched != null && fetched.Count > 0)
            {
                productsDict = fetched.ToDictionary(p => p.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error populating items metadata from inventory service.");
        }

        foreach (var item in allItems)
        {
            if (productsDict.TryGetValue(item.ProductId, out var prod) && prod != null)
            {
                item.IsFractional = prod.IsFractional;
                item.UnitOfMeasure = prod.UnitOfMeasure;
            }
        }
    }

    private async Task PopulateItemsMetadataAsync(Sale sale)
    {
        if (sale != null)
        {
            await PopulateItemsMetadataAsync(new[] { sale });
        }
    }
}


