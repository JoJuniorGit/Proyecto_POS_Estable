using System;
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

namespace Inventory.Module.Services;

public partial class SupplierInvoiceService
{
    private readonly ICurrentUserService? _currentUserService;

    public SupplierInvoiceService(
        InventoryDbContext context,
        ISystemSettingsService systemSettingsService,
        ISupplierProductSimilaritySearch similaritySearch,
        ICurrentUserService currentUserService,
        IProductManagementService productManagementService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(systemSettingsService);
        ArgumentNullException.ThrowIfNull(similaritySearch);
        ArgumentNullException.ThrowIfNull(currentUserService);
        ArgumentNullException.ThrowIfNull(productManagementService);

        _context = context;
        _systemSettingsService = systemSettingsService;
        _similaritySearch = similaritySearch;
        _currentUserService = currentUserService;
        _productManagementService = productManagementService;
    }

    public Task<SupplierInvoiceDetailDto> ConfirmAsync(
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

        return _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            try
            {
                var invoice = await _context.SupplierInvoices
                    .AsSplitQuery()
                    .Include(candidate => candidate.Lines)
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

                var confirmationsByLineId = request.Lines.ToDictionary(confirmation => confirmation.LineId);
                var orderedLines = SupplierInvoiceApplyOrdering.OrderForApply(invoice.Lines);

                // 8.155 (REQ-AIO-01): precargas batch antes del loop. La re-lectura individual del
                // retry xmin sobre entidad fresca se conserva (spec supplier-invoice-apply L64-73).
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

                var aliasesByCode = new Dictionary<string, SupplierProductCode>();
                if (candidateAliasCodes.Count > 0)
                {
                    // Orden por Id + TryAdd: si el arnés no aplica el índice único, gana el alias de
                    // menor Id, igual que el FirstOrDefaultAsync del upsert por línea previo.
                    var existingAliases = await _context.SupplierProductCodes
                        .Where(alias => alias.SupplierId == invoice.SupplierId
                            && alias.Code != null
                            && candidateAliasCodes.Contains(alias.Code))
                        .OrderBy(alias => alias.Id)
                        .ToListAsync(cancellationToken);
                    foreach (var alias in existingAliases)
                    {
                        aliasesByCode.TryAdd(alias.Code!, alias);
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

                var aliasPlan = new Dictionary<string, int>();
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
                    ApplyConversionFactor(line, invoice, confirmation);
                    if (!productsById.TryGetValue(productId, out var product))
                    {
                        throw new KeyNotFoundException($"Product {productId} was not found or has been deleted.");
                    }

                    // 8.155/W1: el retry puede re-leer una instancia fresca del producto; el caché de
                    // precarga debe reflejarla para líneas posteriores que compartan el producto.
                    productsById[productId] = await ApplyApprovedLineAsync(invoice.Id, line, product, confirmation, cancellationToken);
                    // 8.166: aprende/actualiza el factor caja→unidad por proveedor+producto.
                    UpdateRememberedFactor(factorsByProductId, invoice.SupplierId, productId, line.ConversionFactor);
                    if (!string.IsNullOrWhiteSpace(line.SupplierCode))
                    {
                        // 8.146-S5/D8: aprendizaje de alias dentro de la misma transacción;
                        // el rollback del confirm no deja cambios de alias.
                        aliasPlan[line.SupplierCode.Trim()] = productId;
                    }
                }

                foreach (var aliasPlanEntry in aliasPlan)
                {
                    if (aliasesByCode.TryGetValue(aliasPlanEntry.Key, out var alias))
                    {
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

                invoice.Status = SupplierInvoiceStatus.Applied;
                invoice.AppliedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return ToDetailDto(invoice);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                throw;
            }
        });
    }

    private async Task<Product> ApplyApprovedLineAsync(
        int invoiceId,
        SupplierInvoiceLine line,
        Product product,
        ConfirmLineDto confirmation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                // 8.144/8.155: el retry xmin re-lee la entidad fresca dentro de la transacción;
                // la precarga batch solo alimenta el intento inicial.
                var retryProductId = product.Id;
                product = await _context.Products
                    .FirstOrDefaultAsync(
                        candidate => candidate.Id == retryProductId && !candidate.IsDeleted,
                        cancellationToken)
                    ?? throw new KeyNotFoundException($"Product {retryProductId} was not found or has been deleted.");
            }

            var retailMargin = confirmation.MarginRetailOverride ?? product.ProfitMarginRetail;
            var wholesaleMargin = product.HasWholesale
                ? confirmation.MarginWholesaleOverride ?? product.ProfitMarginWholesale
                : retailMargin;

            // 8.165: la cantidad efectiva que entra a stock respeta el factor caja→unidad de la línea.
            var appliedQuantity = line.Quantity * line.ConversionFactor;

            product.CostPriceUSD = line.UnitCostUSD;
            product.ProfitMarginRetail = retailMargin;
            product.ProfitMarginWholesale = wholesaleMargin;
            product.ProfitPercentage = retailMargin;
            product.PriceRetailUSD = PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + retailMargin / 100m));
            product.PriceUSD = product.PriceRetailUSD;
            product.PriceWholesaleUSD = PricingCalculator.RoundPriceUp(line.UnitCostUSD * (1m + wholesaleMargin / 100m));
            product.StockQuantity += appliedQuantity;
            product.UpdatedAt = DateTime.UtcNow;

            var movement = new StockMovement
            {
                Product = product,
                ProductId = product.Id,
                QuantityChange = appliedQuantity,
                NewStockLevel = product.StockQuantity,
                Reason = $"Supplier invoice {invoiceId} applied",
                MovementDate = DateTime.UtcNow,
                UserId = _currentUserService?.UserId
            };
            _context.StockMovements.Add(movement);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return product;
            }
            catch (DbUpdateConcurrencyException exception) when (
                attempt == 0 && exception.Entries.Any(entry => entry.Entity is Product))
            {
                // 8.144: el savepoint revierte la fila insertada en el intento fallido, pero el
                // tracker puede dejarla como guardada; se descarta y el reintento crea otra.
                if (_context.Entry(movement).State != EntityState.Detached)
                {
                    _context.Entry(movement).State = EntityState.Detached;
                }

                _context.Entry(product).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException("8.155/W1: el bucle de retry del apply salió sin resultado.");
    }

    private static void ValidateApprovedLine(SupplierInvoiceLine line, ConfirmLineDto confirmation)
    {
        if (line.UnitCostUSD < 0m)
        {
            throw new ArgumentException("Approved supplier invoice unit cost cannot be negative.", nameof(line));
        }

        if (line.Quantity < 0m)
        {
            throw new ArgumentException("Approved supplier invoice quantity cannot be negative.", nameof(line));
        }

        if (confirmation.MarginRetailOverride is < 0m || confirmation.MarginWholesaleOverride is < 0m)
        {
            throw new ArgumentException("Approved supplier invoice margin overrides cannot be negative.", nameof(confirmation));
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

        if (confirmation.UnitCostDocument is < 0m)
        {
            throw new ArgumentException("The corrected unit cost cannot be negative.");
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
            line.UnitCostUSD = invoice.Currency == CurrencyCodes.Usd
                ? correctedUnitCostDocument
                : PricingCalculator.ToUSD(correctedUnitCostDocument, invoice.AppliedRate);
            line.OcrUnitCostConfidence = null;
        }
    }

    /// <summary>
    /// 8.165: factor de conversión caja→unidad (divisor). El costo unitario aplicado al producto se
    /// divide por el factor (renormalizado con el snapshot de la factura) y ApplyApprovedLineAsync
    /// multiplica la cantidad que entra a stock. El documento queda intacto: UnitCostDocument y
    /// Quantity conservan lo emitido por el proveedor para auditoría; el factor persistido explica
    /// el ajuste.
    /// </summary>
    private static void ApplyConversionFactor(
        SupplierInvoiceLine line,
        SupplierInvoice invoice,
        ConfirmLineDto confirmation)
    {
        // 8.166: null = "sin cambio": conserva el factor ya prefijado en la línea (recordado).
        var factor = confirmation.ConversionFactor ?? line.ConversionFactor;
        line.ConversionFactor = factor;
        if (factor == 1m)
        {
            return;
        }

        var documentUnitCost = line.UnitCostDocument / factor;
        line.UnitCostUSD = invoice.Currency == CurrencyCodes.Usd
            ? documentUnitCost
            : PricingCalculator.ToUSD(documentUnitCost, invoice.AppliedRate);
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
