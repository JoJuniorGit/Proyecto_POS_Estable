using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private const int MaxConcurrencyAttempts = 3;

    // 8.182-T3 (S7): política pura y stateless de redondeo comercial (mismo servicio que el motor
    // bulk; instancia static readonly, drop-in para inyección por DI fuera de esta superficie).
    private static readonly IPricingRoundingPolicyService RoundingPolicy = new PricingRoundingPolicyService();

    public async Task<SupplierInvoiceDetailDto> ConfirmAsync(
        int invoiceId,
        ConfirmSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Lines);
        if (request.Lines.Any(line => line is null))
        {
            throw new ArgumentException("The confirmation contains a null line.", nameof(request));
        }

        if (request.Lines.Select(line => line.LineId).Distinct().Count() != request.Lines.Count)
        {
            throw new ArgumentException("The confirmation contains duplicate supplier invoice line IDs.", nameof(request));
        }

        // 8.176: la moneda viva de la cabecera en revisión viaja opcional; si viene, debe ser canónica.
        // 8.211-T3d: the live header currency travels optionally; only a non-ISO code is rejected
        // here. Profile-specific validity is checked inside the transaction after loading the invoice.
        if (request.Currency is not null
            && CurrencyCodeNormalizer.NormalizeToIso(request.Currency) is null)
        {
            throw new ArgumentException(
                "The supplier invoice currency must be a supported ISO 4217 code.",
                nameof(request));
        }

        if (request.AppliedRate is decimal requestedRate &&
            (requestedRate <= 0m || requestedRate > MaxInvoiceNumericValue))
        {
            throw new ArgumentException("The applied exchange rate must be positive and within the supported range.", nameof(request));
        }

        // 8.211-T3d (F3d): the profile is read once per confirm. A read failure fails closed before
        // any mutation; an unconfigured profile keeps the legacy USD/Bs.S/BCV behavior.
        var monetaryProfile = await ReadMonetaryProfileAsync(cancellationToken);

        // 8.203 (auditoría H-01): la tasa de una corrección no es autoridad del cliente. Se
        // contrasta contra la fuente del perfil dentro de la transacción, después de resolver la
        // moneda objetivo; si la fuente no está disponible se falla cerrado (no se confirma con
        // una tasa sin validar).
        // 8.182-T3 (S7): validación de la regla de redondeo del confirm. ExactMath (default) y
        // UsdPsychological operan sobre el USD recalculado; las reglas VES operan sobre el Bs.S del
        // canal y este flujo no tiene conversión viva aplicable (las facturas Bs.S renormalizan con
        // el snapshot, no con la tasa del día), por lo que se rechazan de forma explícita.
        if (!Enum.IsDefined(request.RoundingRule))
        {
            throw new ArgumentException("The rounding rule is not recognized.", nameof(request));
        }

        if (IsVesCashFriendlyRule(request.RoundingRule))
        {
            throw new ArgumentException(
                "VesCashFriendly rounding rules are not supported when confirming supplier invoices (v1); use ExactMath or UsdPsychological.",
                nameof(request));
        }

        // 8.184-T4 (S7): la regla efectiva de cada línea aprobada es line.RoundingRule ?? request.RoundingRule,
        // así que una regla VES persistida en la línea (elegida al resolverla contra el catálogo) también se
        // rechaza con el mismo contrato, ANTES de abrir la transacción (sin efectos). Se validan solo las
        // líneas aprobadas del request: las no aprobadas no participan del apply.
        var approvedLineIds = request.Lines
            .Where(line => line.IsApproved)
            .Select(line => line.LineId)
            .ToList();
        if (approvedLineIds.Count > 0)
        {
            var storedLineRules = await _context.SupplierInvoiceLines
                .AsNoTracking()
                .Where(line => line.SupplierInvoiceId == invoiceId
                    && approvedLineIds.Contains(line.Id)
                    && line.RoundingRule != null)
                .Select(line => new { line.Id, Rule = line.RoundingRule!.Value })
                .ToListAsync(cancellationToken);

            var offendingLine = storedLineRules.FirstOrDefault(line => IsVesCashFriendlyRule(line.Rule));
            if (offendingLine is not null)
            {
                throw new ArgumentException(
                    $"VesCashFriendly rounding rules are not supported when confirming supplier invoices (v1); line {offendingLine.Id} has a stored VES rule. Use ExactMath or UsdPsychological.",
                    nameof(request));
            }
        }

        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
            {
                _context.ChangeTracker.Clear();

                await using var transaction = _context.Database.IsRelational()
                    ? await _context.Database.BeginTransactionAsync(cancellationToken)
                    : null;

                try
                {
                    var invoice = await _context.SupplierInvoices
                        .AsSplitQuery()
                        .Include(candidate => candidate.Lines)
                        .Include(candidate => candidate.Supplier)
                        .FirstOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken)
                        ?? throw new KeyNotFoundException($"Supplier invoice {invoiceId} was not found.");

                    if (invoice.Status != SupplierInvoiceStatus.Draft)
                    {
                        throw new InvalidOperationException($"Supplier invoice {invoiceId} is not a draft.");
                    }

                    var lineIds = invoice.Lines.Select(line => line.Id).ToHashSet();
                    if (request.Lines.Any(confirmation => !lineIds.Contains(confirmation.LineId)))
                    {
                        throw new ArgumentException("A confirmed line does not belong to this supplier invoice.", nameof(request));
                    }

                    // 8.172/8.176: el revisor puede ajustar la tasa y la moneda del documento durante
                    // la revisión; el confirm conmuta el snapshot del draft (moneda y/o tasa) y fuerza
                    // la renormalización de los costos aprobados con la interpretación final.
                    // 8.211-T3d (F3d): resolve the target currency and its effective snapshot rate
                    // against the profile (or the legacy USD/Bs.S contract) BEFORE mutating the
                    // invoice. A secondary validates its effective rate against the profile source.
                    var currencyResolution = await ResolveConfirmCurrencyAsync(
                        invoice,
                        request,
                        monetaryProfile,
                        cancellationToken);
                    var targetCurrency = currencyResolution.Currency;
                    var targetAppliedRate = currencyResolution.AppliedRate;
                    var interpretationChanged = !string.Equals(
                        CurrencyCodeNormalizer.NormalizeToIso(invoice.Currency),
                        CurrencyCodeNormalizer.NormalizeToIso(targetCurrency),
                        StringComparison.Ordinal);
                    var appliedRateChanged = interpretationChanged || invoice.AppliedRate != targetAppliedRate;
                    if (appliedRateChanged)
                    {
                        invoice.AppliedRate = targetAppliedRate;
                    }

                    if (!string.Equals(invoice.Currency, targetCurrency, StringComparison.Ordinal))
                    {
                        invoice.Currency = targetCurrency;
                    }

                    var confirmationsByLineId = request.Lines.ToDictionary(confirmation => confirmation.LineId);
                    var orderedLines = SupplierInvoiceApplyOrdering.OrderForApply(invoice.Lines);

                    // 8.155 (REQ-AIO-01): precargas batch antes del loop.
                    var applicableLines = orderedLines
                        .Where(line => confirmationsByLineId.TryGetValue(line.Id, out var confirmation)
                            && confirmation.IsApproved
                            && line.Status != SupplierInvoiceLineStatus.Conflict)
                        .ToList();

                    var applicableProductIds = applicableLines
                        .Where(line => line.ResolvedProductId is not null)
                        .Select(line => line.ResolvedProductId!.Value)
                        .Distinct()
                        .ToList();

                    var productsById = new Dictionary<int, Product>();
                    if (applicableProductIds.Count > 0)
                    {
                        productsById = await _context.Products
                            .Include(product => product.ParentProduct)
                            .Where(product => !product.IsDeleted && applicableProductIds.Contains(product.Id))
                            .ToDictionaryAsync(product => product.Id, cancellationToken);
                    }

                    // 8.155 (REQ-AIO-02): alias existentes del proveedor para los códigos candidatos en
                    // UNA lectura trackeada; el upsert se resuelve en memoria y se persiste con el
                    // guardado final (una sola escritura), sin cambios para el flujo de creación.
                    var candidateAliasCodes = applicableLines
                        .Where(line => !string.IsNullOrWhiteSpace(line.SupplierCode))
                        .Select(line => line.SupplierCode!.Trim())
                        .Distinct()
                        .ToList();

                    var aliasesByCode = new Dictionary<string, List<SupplierProductCode>>(StringComparer.Ordinal);
                    if (candidateAliasCodes.Count > 0)
                    {
                        // Orden por Id: si el arnés no aplica el índice único, el primer elemento es el de menor Id.
                        var existingAliases = await _context.SupplierProductCodes
                            .Where(alias => alias.SupplierId == invoice.SupplierId
                                && alias.Code != null
                                && candidateAliasCodes.Contains(alias.Code))
                            .OrderBy(alias => alias.Id)
                            .ToListAsync(cancellationToken);
                        foreach (var alias in existingAliases)
                        {
                            if (!aliasesByCode.TryGetValue(alias.Code!, out var list))
                            {
                                list = new List<SupplierProductCode>();
                                aliasesByCode[alias.Code!] = list;
                            }

                            list.Add(alias);
                        }
                    }

                    // 8.166: factores recordados por proveedor+producto para los candidatos del apply
                    // (upsert en memoria; se persiste con el guardado final de la transacción).
                    var factorsByProductId = new Dictionary<int, SupplierProductFactor>();
                    if (applicableProductIds.Count > 0)
                    {
                        var existingFactors = await _context.SupplierProductFactors
                            .Where(factor => factor.SupplierId == invoice.SupplierId
                                && applicableProductIds.Contains(factor.ProductId))
                            .ToListAsync(cancellationToken);
                        foreach (var factor in existingFactors)
                        {
                            factorsByProductId.TryAdd(factor.ProductId, factor);
                        }
                    }

                    var aliasPlan = new Dictionary<string, int>(StringComparer.Ordinal);
                    var multiProductAliasPlan = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    foreach (var line in orderedLines)
                    {
                        if (!confirmationsByLineId.TryGetValue(line.Id, out var confirmation))
                        {
                            line.IsApproved = false;
                            continue;
                        }

                        line.IsApproved = confirmation.IsApproved;
                        if (!confirmation.IsApproved)
                        {
                            continue;
                        }

                        ValidateApprovedLine(line, confirmation);
                        line.MarginRetailOverride = confirmation.MarginRetailOverride;
                        line.MarginWholesaleOverride = confirmation.MarginWholesaleOverride;
                        if (line.Status == SupplierInvoiceLineStatus.Conflict || line.ResolvedProductId is not int productId)
                        {
                            continue;
                        }

                        ApplyCorrections(line, invoice, confirmation);
                        ApplyConversionFactor(line, invoice, confirmation, appliedRateChanged);
                        if (!productsById.TryGetValue(productId, out var product))
                        {
                            throw new KeyNotFoundException($"Product {productId} was not found or has been deleted.");
                        }

                        // 8.184-T4 (S7): precedencia línea ?? factura — la regla persistida al
                        // resolver la línea contra el catálogo pisa la regla elegida en el confirm.
                        ApplyApprovedLine(invoice.Id, line, product, confirmation, line.RoundingRule ?? request.RoundingRule);
                        // 8.166: aprende/actualiza el factor caja→unidad por proveedor+producto.
                        UpdateRememberedFactor(factorsByProductId, invoice.SupplierId, productId, line.ConversionFactor);
                        if (!string.IsNullOrWhiteSpace(line.SupplierCode))
                        {
                            var normalizedCode = line.SupplierCode.Trim();
                            if (line.MatchMethod == MatchMethod.SupplierCode)
                            {
                                if (!multiProductAliasPlan.TryGetValue(normalizedCode, out var multiProductIds))
                                {
                                    multiProductIds = new List<int>();
                                    multiProductAliasPlan[normalizedCode] = multiProductIds;
                                }

                                if (!multiProductIds.Contains(productId))
                                {
                                    multiProductIds.Add(productId);
                                }
                            }
                            else
                            {
                                // 8.146-S5/D8: aprendizaje de alias dentro de la misma transacción;
                                // el rollback del confirm no deja cambios de alias.
                                aliasPlan[normalizedCode] = productId;
                            }
                        }
                    }

                    foreach (var aliasPlanEntry in aliasPlan.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        if (aliasesByCode.TryGetValue(aliasPlanEntry.Key, out var existingList) && existingList.Count > 0)
                        {
                            var alias = existingList[0];
                            if (alias.ProductId != aliasPlanEntry.Value)
                            {
                                alias.ProductId = aliasPlanEntry.Value;
                            }
                        }
                        else
                        {
                            _context.SupplierProductCodes.Add(new SupplierProductCode
                            {
                                SupplierId = invoice.SupplierId,
                                Code = aliasPlanEntry.Key,
                                ProductId = aliasPlanEntry.Value
                            });
                        }
                    }

                    foreach (var multiAliasEntry in multiProductAliasPlan.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        aliasesByCode.TryGetValue(multiAliasEntry.Key, out var existingList);
                        foreach (var targetProductId in multiAliasEntry.Value)
                        {
                            if (existingList is not null && existingList.Any(alias => alias.ProductId == targetProductId))
                            {
                                continue;
                            }

                            var createdAlias = new SupplierProductCode
                            {
                                SupplierId = invoice.SupplierId,
                                Code = multiAliasEntry.Key,
                                ProductId = targetProductId
                            };
                            _context.SupplierProductCodes.Add(createdAlias);
                            existingList ??= new List<SupplierProductCode>();
                            aliasesByCode[multiAliasEntry.Key] = existingList;
                            existingList.Add(createdAlias);
                        }
                    }

                    // 8.167: recuerda la moneda facturada por el proveedor (prefijo de la próxima factura).
                    if (invoice.Supplier is not null
                        && !string.Equals(invoice.Supplier.InvoiceCurrency, invoice.Currency, StringComparison.Ordinal))
                    {
                        invoice.Supplier.InvoiceCurrency = invoice.Currency;
                        invoice.Supplier.UpdatedAt = DateTime.UtcNow;
                    }

                    invoice.Status = SupplierInvoiceStatus.Applied;
                    invoice.AppliedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    InvalidateProductCache(productsById.Values
                        .Concat(productsById.Values
                            .Where(product => product.ParentProduct is not null)
                            .Select(product => product.ParentProduct!)));
                    _logger.LogInformation(
                        "Confirmed supplier invoice {InvoiceId} for supplier {SupplierId} ({ApprovedCount} approved lines).",
                        invoice.Id,
                        invoice.SupplierId,
                        applicableLines.Count);

                    return ToDetailDto(invoice);
                }
                catch (DbUpdateConcurrencyException exception) when (
                    attempt + 1 < MaxConcurrencyAttempts && exception.Entries.Any(entry => entry.Entity is Product))
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }

                    _context.ChangeTracker.Clear();
                    _logger.LogWarning(
                        exception,
                        "Optimistic concurrency conflict applying supplier invoice {InvoiceId} (attempt {Attempt}/{MaxAttempts}).",
                        invoiceId,
                        attempt + 1,
                        MaxConcurrencyAttempts);
                    await Task.Delay(25 * (attempt + 1), cancellationToken);
                }
                catch
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    else
                    {
                        _context.ChangeTracker.Clear();
                    }

                    throw;
                }
            }

            throw new InvalidOperationException("8.155/W1: el bucle de retry del apply salió sin resultado.");
        });
    }

    /// <summary>8.184-T4 (S7): las reglas VES operan sobre el Bs.S y no están soportadas en el confirm (v1).</summary>
    private static bool IsVesCashFriendlyRule(RoundingRuleType rule) =>
        rule is RoundingRuleType.VesCashFriendlyInteger
            or RoundingRuleType.VesCashFriendlyStep5
            or RoundingRuleType.VesCashFriendlyStep10;

    private void ApplyApprovedLine(
        int invoiceId,
        SupplierInvoiceLine line,
        Product product,
        ConfirmLineDto confirmation,
        RoundingRuleType roundingRule)
    {
        var retailMargin = confirmation.MarginRetailOverride ?? product.ProfitMarginRetail;
        var wholesaleMargin = confirmation.MarginWholesaleOverride
            ?? (product.HasWholesale ? product.ProfitMarginWholesale : retailMargin);
        if (confirmation.MarginWholesaleOverride.HasValue && confirmation.MarginWholesaleOverride.Value != retailMargin)
        {
            product.HasWholesale = true;
        }

        // 8.165: la cantidad efectiva que entra a stock respeta el factor caja→unidad de la línea.
        var appliedQuantity = line.Quantity * line.ConversionFactor;

        if (!string.IsNullOrWhiteSpace(confirmation.Name)
            && !string.IsNullOrWhiteSpace(line.Name)
            && line.OldCostPriceUSD == 0m
            && line.OldStockQuantity == 0m)
        {
            product.Name = line.Name;
        }

        product.Cost = line.UnitCostUSD;
        product.CostPriceUSD = line.UnitCostUSD;

        // 8.182-T3 (S7): precios finales de la línea (redondeo del confirm + salvaguarda Detal/Mayor).
        // 8.183 (R3): los márgenes persistidos son SIEMPRE los del revisor/operados (verbatim); el
        // redondeo solo mueve precios. Con ExactMath = flujo vigente byte a byte.
        var pricing = ResolveApprovedLinePricing(
            line.UnitCostUSD,
            retailMargin,
            wholesaleMargin,
            product.HasWholesale,
            roundingRule);
        product.ProfitMarginRetail = pricing.RetailMargin;
        product.ProfitMarginWholesale = pricing.WholesaleMargin;
        // Legacy: ProfitPercentage espeja el margen retail del revisor (nunca el efectivo del redondeo).
        product.ProfitPercentage = pricing.RetailMargin;
        product.PriceRetailUSD = pricing.RetailPrice;
        product.PriceUSD = product.PriceRetailUSD;
        product.PriceWholesaleUSD = pricing.WholesalePrice;
        product.UpdatedAt = DateTime.UtcNow;
        line.SuggestedRetailPriceUSD = product.PriceRetailUSD;
        line.SuggestedWholesalePriceUSD = product.PriceWholesaleUSD;

        if (appliedQuantity > 0m)
        {
            // SRE-09: Redirect stock increment to shared parent product when applicable.
            var stockTarget = product.ParentProduct is { IsDeleted: false, IsStockShared: true } parent
                ? parent
                : product;
            stockTarget.StockQuantity += appliedQuantity;
            stockTarget.UpdatedAt = DateTime.UtcNow;

            var movement = new StockMovement
            {
                Product = stockTarget,
                ProductId = stockTarget.Id,
                QuantityChange = appliedQuantity,
                NewStockLevel = stockTarget.StockQuantity,
                Reason = $"Supplier invoice {invoiceId} applied",
                MovementDate = DateTime.UtcNow,
                UserId = _currentUserService?.UserId,
                // 8.177-T1: entrada por factura proveedor = Importación / Entrada por Compra, con
                // referencia cruzada a la factura y costo unitario renormalizado de la línea
                // (snapshot Currency/AppliedRate de la factura, nunca la tasa BCV vigente).
                OriginType = StockMovementOriginType.Import,
                ReasonCode = StockMovementReason.PurchaseEntry,
                SupplierInvoiceId = invoiceId,
                UnitCostUSD = line.UnitCostUSD
            };
            _context.StockMovements.Add(movement);
        }
    }

    // 8.183 (R3): el margen efectivo del redondeo NUNCA se persiste ni se recalcula al confirmar:
    // los márgenes de la entidad son los del revisor/operados (verbatim). La regla y la salvaguarda
    // Detal/Mayor solo mueven PRECIOS.

    /// <summary>
    /// 8.182-T3 (S7) / 8.183 (R3) / 8.184-T4 (S7): resuelve los precios finales de una línea
    /// aprobada a partir de la regla EFECTIVA (línea ?? factura — la precedencia la resuelve el
    /// llamador). Los matemáticos usan el techo vigente (RoundPriceUp, 8.104); la regla de redondeo
    /// y la salvaguarda Detal/Mayor solo mueven PRECIOS — los márgenes devueltos son verbatim los
    /// del revisor (el margen efectivo del redondeo no se persiste: raíz del Efecto Trinquete
    /// corregida en 8.183). Con ExactMath el resultado es idéntico al flujo actual. El piso de
    /// margen no aplica en facturas v1 (el margen del revisor manda): enforceFloor = false.
    /// </summary>
    private static (decimal RetailPrice, decimal WholesalePrice, decimal RetailMargin, decimal WholesaleMargin) ResolveApprovedLinePricing(
        decimal unitCostUsd,
        decimal retailMargin,
        decimal wholesaleMargin,
        bool hasWholesale,
        RoundingRuleType roundingRule)
    {
        var retailMath = PricingCalculator.RoundPriceUp(unitCostUsd * (1m + retailMargin / 100m));
        var wholesaleMath = PricingCalculator.RoundPriceUp(unitCostUsd * (1m + wholesaleMargin / 100m));
        if (roundingRule == RoundingRuleType.ExactMath)
        {
            return (retailMath, wholesaleMath, retailMargin, wholesaleMargin);
        }

        var retailRounding = RoundingPolicy.Round(retailMath, roundingRule, unitCostUsd, null, false);
        var wholesaleRounding = RoundingPolicy.Round(wholesaleMath, roundingRule, unitCostUsd, null, false);
        var retailMoved = retailRounding.RoundedValue != retailMath;
        var wholesaleMoved = wholesaleRounding.RoundedValue != wholesaleMath;

        var guard = ApplyWholesaleCollisionGuard(
            retailRounding.RoundedValue,
            wholesaleRounding.RoundedValue,
            unitCostUsd,
            roundingRule,
            hasWholesale,
            retailMoved || wholesaleMoved);
        if (!guard.Held)
        {
            // Degradación determinista: la línea vuelve a los matemáticos con los márgenes del
            // revisor intactos (el confirm nunca se rompe por la política de redondeo).
            return (retailMath, wholesaleMath, retailMargin, wholesaleMargin);
        }

        // 8.183 (R3): los márgenes del revisor viajan verbatim; la regla —o la salvaguarda, que puede
        // bajar el precio al mayor— solo afecta a los precios del canal.
        return (retailRounding.RoundedValue, guard.WholesalePrice, retailMargin, wholesaleMargin);
    }

    /// <summary>
    /// 8.182-T3 (S7): salvaguarda Detal/Mayor (mismo patrón que el motor bulk). Si el Mayor
    /// redondeado queda ≥ al Detal redondeado se baja al escalón inmediato inferior siempre que
    /// quede por encima del costo; Held = false cuando el invariante no puede mantenerse (la línea
    /// degrada a sin redondeo). Sin mayor habilitado o sin movimiento de la regla no interviene.
    /// </summary>
    private static (decimal WholesalePrice, bool Held, bool Lowered) ApplyWholesaleCollisionGuard(
        decimal retailPrice,
        decimal wholesalePrice,
        decimal unitCostUsd,
        RoundingRuleType roundingRule,
        bool hasWholesale,
        bool anyChannelMoved)
    {
        if (!hasWholesale || !anyChannelMoved || wholesalePrice < retailPrice)
        {
            return (wholesalePrice, true, false);
        }

        var lowered = RoundingPolicy.FindLowerStep(wholesalePrice, roundingRule);
        return lowered is > 0m && (unitCostUsd <= 0m || lowered.Value > unitCostUsd)
            ? (lowered.Value, true, true)
            : (wholesalePrice, false, false);
    }

    /// <summary>
    /// 8.211-T3d (F3d): resolves the final document currency and snapshot rate for the confirm.
    /// Single accepts only its primary (identity rate); Dual accepts the primary (identity) or the
    /// secondary (positive rate validated against the profile source); no configured profile keeps
    /// the historical USD/Bs.S contract. A target primary/USD always forces rate 1.
    /// </summary>
    private async Task<(string Currency, decimal AppliedRate)> ResolveConfirmCurrencyAsync(
        SupplierInvoice invoice,
        ConfirmSupplierInvoiceRequestDto request,
        MonetaryProfileDto? profile,
        CancellationToken cancellationToken)
    {
        var targetIso = CurrencyCodeNormalizer.NormalizeToIso(request.Currency ?? invoice.Currency)
            ?? throw new ArgumentException(
                "The supplier invoice currency is not a supported ISO 4217 code.",
                nameof(request));

        if (profile is null)
        {
            return await ResolveLegacyConfirmCurrencyAsync(invoice, request, targetIso, cancellationToken);
        }

        var primaryIso = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode)
            ?? throw new InvalidOperationException("The active monetary profile has an invalid primary currency.");

        if (profile.Mode == MonetaryMode.Single)
        {
            if (!string.Equals(targetIso, primaryIso, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The supplier invoice currency must be {ToPresentationCurrency(primaryIso)} for the active monetary profile.",
                    nameof(request));
            }

            // Single is an identity regime: any request rate is ignored and the snapshot stays 1.
            return (ToPresentationCurrency(primaryIso), 1m);
        }

        var secondaryIso = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode)
            ?? throw new InvalidOperationException("The active monetary profile has no valid secondary currency.");

        if (string.Equals(targetIso, primaryIso, StringComparison.Ordinal))
        {
            return (ToPresentationCurrency(primaryIso), 1m);
        }

        if (!string.Equals(targetIso, secondaryIso, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The supplier invoice currency must be {ToPresentationCurrency(primaryIso)} or {ToPresentationCurrency(secondaryIso)} for the active monetary profile.",
                nameof(request));
        }

        var presentation = ToPresentationCurrency(secondaryIso);
        var currencyChanged = !string.Equals(
            CurrencyCodeNormalizer.NormalizeToIso(invoice.Currency),
            targetIso,
            StringComparison.Ordinal);
        var appliedRate = request.AppliedRate is decimal overrideRate
            ? PricingCalculator.RoundExchangeRate(overrideRate, PricingCalculator.RateSnapshotDecimals)
            : currencyChanged
                ? throw new ArgumentException(GetSecondaryRateRequiredMessage(presentation), nameof(request))
                : PricingCalculator.RoundExchangeRate(invoice.AppliedRate, PricingCalculator.RateSnapshotDecimals);

        if (appliedRate <= 0m)
        {
            throw new ArgumentException(GetSecondaryRateRequiredMessage(presentation), nameof(request));
        }

        // 8.211-T3d: a Dual secondary validates its effective rate against the profile source:
        // VES stays on BCV, the other secondaries use the exact daily manual pair (fail-closed).
        await ValidateSecondaryRateAgainstProfileAsync(
            appliedRate,
            primaryIso,
            secondaryIso,
            cancellationToken);

        return (presentation, appliedRate);
    }

    /// <summary>
    /// 8.211-T3d (F3d): legacy/no-profile confirm. USD keeps rate 1; Bs.S (VES aliases included)
    /// requires a positive rate and keeps the historical BCV ±10% validation only when the client
    /// sends a correction (the staged snapshot was already accepted at creation time).
    /// </summary>
    private async Task<(string Currency, decimal AppliedRate)> ResolveLegacyConfirmCurrencyAsync(
        SupplierInvoice invoice,
        ConfirmSupplierInvoiceRequestDto request,
        string targetIso,
        CancellationToken cancellationToken)
    {
        if (string.Equals(targetIso, CurrencyCodes.Usd, StringComparison.Ordinal))
        {
            return (CurrencyCodes.Usd, 1m);
        }

        if (!string.Equals(targetIso, "VES", StringComparison.Ordinal))
        {
            throw new ArgumentException("The supplier invoice currency must be USD or Bs.S.", nameof(request));
        }

        var currencyChanged = !string.Equals(
            CurrencyCodeNormalizer.NormalizeToIso(invoice.Currency),
            targetIso,
            StringComparison.Ordinal);
        var appliedRate = request.AppliedRate is decimal overrideRate
            ? PricingCalculator.RoundExchangeRate(overrideRate, PricingCalculator.RateSnapshotDecimals)
            : currencyChanged
                ? throw new ArgumentException(GetSecondaryRateRequiredMessage(CurrencyCodes.BsS), nameof(request))
                : PricingCalculator.RoundExchangeRate(invoice.AppliedRate, PricingCalculator.RateSnapshotDecimals);

        if (appliedRate <= 0m)
        {
            throw new ArgumentException(GetSecondaryRateRequiredMessage(CurrencyCodes.BsS), nameof(request));
        }

        if (request.AppliedRate is not null)
        {
            await ValidateRequestedRateAgainstOfficialAsync(appliedRate, cancellationToken);
        }

        return (CurrencyCodes.BsS, appliedRate);
    }

    /// <summary>
    /// 8.211-T3d (F3d): Dual VES keeps BCV; COP/MXN/EUR resolve the exact daily manual pair.
    /// A missing service or quote fails closed, and the 18,4 invoice rate is compared with the
    /// same ±10% tolerance as BCV.
    /// </summary>
    private async Task ValidateSecondaryRateAgainstProfileAsync(
        decimal effectiveRate,
        string primaryIso,
        string secondaryIso,
        CancellationToken cancellationToken)
    {
        if (string.Equals(secondaryIso, "VES", StringComparison.Ordinal))
        {
            await ValidateRequestedRateAgainstOfficialAsync(effectiveRate, cancellationToken);
            return;
        }

        if (_currencyRateService is null)
        {
            throw new InvalidOperationException(
                $"No hay servicio de tasas manuales configurado para el par {primaryIso}/{secondaryIso}.");
        }

        CurrencyRateQuote quote;
        try
        {
            quote = await _currencyRateService.GetRateAsync(
                primaryIso,
                secondaryIso,
                null,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[SupplierInvoice] No se pudo obtener la tasa manual del par {Pair} para validar el confirm.",
                $"{primaryIso}/{secondaryIso}");
            throw new InvalidOperationException(
                $"No se pudo validar la tasa de la factura contra la tasa manual del par {primaryIso}/{secondaryIso}. Verifique la cotización manual del día.",
                ex);
        }

        var officialRate = PricingCalculator.RoundExchangeRate(
            quote.Rate,
            PricingCalculator.RateSnapshotDecimals);
        if (officialRate <= 0m)
        {
            throw new InvalidOperationException(
                $"No hay una tasa manual del día registrada para validar la tasa de la factura (par {primaryIso}/{secondaryIso}).");
        }

        decimal deviation = Math.Abs(effectiveRate - officialRate) / officialRate;
        if (deviation > MaxConfirmRateDeviation)
        {
            throw new ArgumentException(
                $"La tasa {effectiveRate:0.####} excede ±{MaxConfirmRateDeviation:P0} de la tasa manual del par {primaryIso}/{secondaryIso} ({officialRate:0.####}). " +
                "Corrija la tasa o solicite autorización antes de confirmar la factura.",
                nameof(effectiveRate));
        }
    }

    private static string GetSecondaryRateRequiredMessage(string presentationCurrency) =>
        $"An applied exchange rate greater than zero is required for supplier invoices in {presentationCurrency}.";

    /// <summary>
    /// 8.203: una corrección de tasa solo se admite dentro de ±10% de la tasa oficial del día.
    /// Sin proveedor (tests directos/legacy) se conserva el contrato previo; en DI de producción
    /// el proveedor siempre está registrado.
    /// </summary>
    private async Task ValidateRequestedRateAgainstOfficialAsync(
        decimal requestedRate,
        CancellationToken cancellationToken)
    {
        if (_rateProvider is null)
        {
            return;
        }

        decimal officialRate;
        try
        {
            officialRate = await _rateProvider.GetEffectiveTodayRateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[SupplierInvoice] No se pudo obtener la tasa oficial para validar el confirm de la factura.");
            throw new InvalidOperationException(
                "No se pudo validar la tasa de la factura contra la tasa oficial del día. Reintente cuando la tasa esté disponible.",
                ex);
        }

        if (officialRate <= 0m)
        {
            throw new InvalidOperationException(
                "No hay una tasa oficial del día registrada para validar la tasa de la factura.");
        }

        decimal deviation = Math.Abs(requestedRate - officialRate) / officialRate;
        if (deviation > MaxConfirmRateDeviation)
        {
            throw new ArgumentException(
                $"La tasa {requestedRate:0.####} excede ±{MaxConfirmRateDeviation:P0} de la tasa oficial del día ({officialRate:0.####}). " +
                "Corrija la tasa o solicite autorización antes de confirmar la factura.",
                nameof(requestedRate));
        }
    }

    private static void ValidateApprovedLine(SupplierInvoiceLine line, ConfirmLineDto confirmation)
    {
        if (line.UnitCostUSD < 0m)
        {
            throw new ArgumentException("Approved supplier invoice unit cost cannot be negative.", nameof(line));
        }

        if (line.UnitCostUSD > MaxInvoiceNumericValue)
        {
            throw new ArgumentException("Approved supplier invoice unit cost exceeds the maximum supported value.", nameof(line));
        }

        if (line.Quantity < 0m)
        {
            throw new ArgumentException("Approved supplier invoice quantity cannot be negative.", nameof(line));
        }

        if (line.Quantity > MaxInvoiceNumericValue)
        {
            throw new ArgumentException("Approved supplier invoice quantity exceeds the maximum supported value.", nameof(line));
        }

        if (confirmation.MarginRetailOverride is < 0m || confirmation.MarginWholesaleOverride is < 0m)
        {
            throw new ArgumentException("Approved supplier invoice margin overrides cannot be negative.", nameof(confirmation));
        }

        if (confirmation.MarginRetailOverride > MaxMarginOverrideValue ||
            confirmation.MarginWholesaleOverride > MaxMarginOverrideValue)
        {
            throw new ArgumentException("Approved supplier invoice margin overrides exceed the maximum supported value.", nameof(confirmation));
        }

        // 8.165: el factor de conversión caja→unidad es un divisor: debe ser positivo y acotado.
        if (confirmation.ConversionFactor is decimal conversionFactor &&
            (conversionFactor <= 0m || conversionFactor > 10000m))
        {
            throw new ArgumentException("The conversion factor must be positive (0 < factor <= 10000).", nameof(confirmation));
        }
    }

    /// <summary>
    /// 8.147-T7b/S6: correcciones del revisor sobre campos OCR verificados contra la imagen.
    /// Solo datos: no re-clasifica ni re-matchea (la línea ya está resuelta) y ApplyApprovedLineAsync
    /// consume Quantity/UnitCostUSD corregidos. El costo documental se renormaliza con el snapshot
    /// de la factura (Currency/AppliedRate), nunca con la tasa BCV vigente. Todos los rechazos se
    /// validan antes de mutar la línea.
    /// </summary>
    private static void ApplyCorrections(
        SupplierInvoiceLine line,
        SupplierInvoice invoice,
        ConfirmLineDto confirmation)
    {
        var correctedName = confirmation.Name?.Trim();
        if (confirmation.Name is not null && string.IsNullOrEmpty(correctedName))
        {
            throw new ArgumentException("The corrected product name cannot be blank.");
        }

        if (correctedName is { Length: > 100 })
        {
            throw new ArgumentException("The corrected product name cannot exceed 100 characters.");
        }

        if (confirmation.Quantity is < 0m)
        {
            throw new ArgumentException("The corrected quantity cannot be negative.");
        }

        if (confirmation.Quantity > MaxInvoiceNumericValue)
        {
            throw new ArgumentException("The corrected quantity exceeds the maximum supported value.");
        }

        if (confirmation.UnitCostDocument is < 0m)
        {
            throw new ArgumentException("The corrected unit cost cannot be negative.");
        }

        if (confirmation.UnitCostDocument > MaxInvoiceNumericValue)
        {
            throw new ArgumentException("The corrected unit cost exceeds the maximum supported value.");
        }

        if (correctedName is not null)
        {
            line.Name = correctedName;
            // El campo fue verificado/corregido por el revisor: su confianza OCR deja de aplicar.
            line.OcrNameConfidence = null;
        }

        if (confirmation.Quantity is decimal correctedQuantity)
        {
            line.Quantity = correctedQuantity;
            line.OcrQuantityConfidence = null;
        }

        if (confirmation.UnitCostDocument is decimal correctedUnitCostDocument)
        {
            line.UnitCostDocument = correctedUnitCostDocument;
            line.UnitCostUSD = PricingCalculator.CalculateInvoiceUnitCostUsd(
                correctedUnitCostDocument,
                1m,
                invoice.Currency,
                invoice.AppliedRate);
            line.OcrUnitCostConfidence = null;
        }
    }

    /// <summary>
    /// 8.165/8.172: factor de conversión caja→unidad (divisor) y renormalización cuando cambia la
    /// tasa aplicada de una factura Bs.S. El costo unitario aplicado al producto se divide por el
    /// factor (renormalizado con el snapshot de la factura) y ApplyApprovedLine multiplica la
    /// cantidad que entra a stock.
    /// </summary>
    private static void ApplyConversionFactor(
        SupplierInvoiceLine line,
        SupplierInvoice invoice,
        ConfirmLineDto confirmation,
        bool appliedRateChanged = false)
    {
        // 8.166: null = "sin cambio": conserva el factor ya prefijado en la línea (recordado).
        var factor = confirmation.ConversionFactor ?? line.ConversionFactor;
        line.ConversionFactor = factor;
        if (factor == 1m && !appliedRateChanged)
        {
            return;
        }

        line.UnitCostUSD = PricingCalculator.CalculateInvoiceUnitCostUsd(
            line.UnitCostDocument,
            factor,
            invoice.Currency,
            invoice.AppliedRate);
    }

    /// <summary>
    /// 8.166: recuerda el factor caja→unidad por proveedor+producto dentro de la misma transacción
    /// (el rollback del confirm no deja cambios). Se actualiza también cuando el revisor vuelve a 1
    /// (cambio de facturación); un 1 sin registro previo no crea una fila nueva.
    /// </summary>
    private void UpdateRememberedFactor(
        Dictionary<int, SupplierProductFactor> factorsByProductId,
        int supplierId,
        int productId,
        decimal conversionFactor)
    {
        if (factorsByProductId.TryGetValue(productId, out var existing))
        {
            if (existing.ConversionFactor != conversionFactor)
            {
                existing.ConversionFactor = conversionFactor;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            return;
        }

        if (conversionFactor == 1m)
        {
            return;
        }

        var created = new SupplierProductFactor
        {
            SupplierId = supplierId,
            ProductId = productId,
            ConversionFactor = conversionFactor
        };
        factorsByProductId[productId] = created;
        _context.SupplierProductFactors.Add(created);
    }
}
