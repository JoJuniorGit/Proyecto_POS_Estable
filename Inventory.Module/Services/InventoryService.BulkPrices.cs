using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

// 8.181-T1 (S1-S4): edición masiva de precios del catálogo.
// - Preview: solo lectura (sin escrituras).
// - Apply: UNA transacción con execution strategy, all-or-nothing; camino rastreado para que el
//   token xmin de PostgreSQL evalúe los conflictos (a diferencia de ExecuteUpdate).
// - Preview y apply comparten ComputeBulkPricePlanItem: mismo motor de cálculo (S4).
// - Reuso de las MISMAS rutinas del update individual: ValidateAndCalculateProductPrices,
//   PricingCalculator.RoundPriceUp / ToBsSCeiling (S2).
// 8.183-T1 (R1/R3): el redondeo no muta datos persistidos — el margen persistido es SIEMPRE el
// objetivo/operado y el Bs.S almacenado, el canónico 8.104; las reglas VES se exponen como
// proyección en caliente (CashFriendlyAfterBsS del cambio), nunca como escritura.
// Spec V0.15: los márgenes Detal/Mayor operan en PUNTOS PORCENTUALES aditivos (actual ± Δ) y sus
// reducciones se clipean al piso del canal o a 0; precios/costo conservan su semántica vigente y el
// pipeline de redondeo/piso/Bs.S queda intacto aguas abajo (solo cambia el INPUT del margen).
// Spec V0.15 (split de capas): el redondeo USD usa las reglas por canal (RetailRoundingRule/
// WholesaleRoundingRule) y la proyección Bs.S en caliente usa CashBsRoundingRule (etapa
// independiente, nunca persistida; VES en la regla del canal quedó como vía defensiva sin efecto).
public partial class InventoryService
{
    private const int MaxBulkPriceSelectionSize = 1000;
    private const decimal MaxBulkPriceAmount = 1_000_000m;
    private const decimal MaxBulkPriceMargin = 1000m;

    // 8.182-T1 (S1): política pura y stateless (drop-in para inyección futura por DI).
    private static readonly IPricingRoundingPolicyService RoundingPolicy = new PricingRoundingPolicyService();

    public async Task<BulkPriceUpdateResultDto> PreviewBulkPriceUpdateAsync(BulkPriceUpdateRequestDto request, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        ValidateBulkPriceRequest(request);

        var selectionIds = await ResolveBulkPriceSelectionIdsAsync(request, cancellationToken);
        var exchangeRate = await GetTodayExchangeRateAsync(cancellationToken);
        if (selectionIds.Count == 0)
        {
            return BuildBulkPriceResult(exchangeRate, 0, Array.Empty<BulkPricePlanItem>());
        }

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => selectionIds.Contains(p.Id))
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        var plan = await BuildBulkPricePlanAsync(products, request, exchangeRate, cancellationToken);
        return BuildBulkPriceResult(exchangeRate, selectionIds.Count, plan);
    }

    public async Task<BulkPriceUpdateResultDto> BulkUpdatePricesAsync(BulkPriceUpdateRequestDto request, CancellationToken cancellationToken = default)
    {
        EnsureCatalogMutationPermission();
        ValidateBulkPriceRequest(request);

        var selectionIds = await ResolveBulkPriceSelectionIdsAsync(request, cancellationToken);
        var exchangeRate = await GetTodayExchangeRateAsync(cancellationToken);
        if (selectionIds.Count == 0)
        {
            return BuildBulkPriceResult(exchangeRate, 0, Array.Empty<BulkPricePlanItem>());
        }

        BulkPriceUpdateResultDto? result = null;

        // 8.16-H02: la transacción manual vive DENTRO de CreateExecutionStrategy().ExecuteAsync()
        // para no lanzar InvalidOperationException bajo NpgsqlRetryingExecutionStrategy.
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            await using var transaction = _context.Database.IsRelational() ? await _context.Database.BeginTransactionAsync(cancellationToken) : null;

            var products = await _context.Products
                .Where(p => selectionIds.Contains(p.Id))
                .OrderBy(p => p.Id)
                .ToListAsync(cancellationToken);

            var plan = await BuildBulkPricePlanAsync(products, request, exchangeRate, cancellationToken);

            var errorItem = plan.FirstOrDefault(item => item.Row.IsError);
            if (errorItem != null)
            {
                // All-or-nothing (S1): si algún producto resulta inválido no se escribe NINGUNO.
                throw new ArgumentException($"La actualización masiva no se aplicó: {errorItem.Product.Name} ({errorItem.Product.SKU}): {errorItem.Row.SkipReason}");
            }

            foreach (var item in plan.Where(item => item.HasChanges))
            {
                ApplyBulkPriceState(item);
            }

            await PropagateBulkPriceGroupsAsync(plan, exchangeRate, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            result = BuildBulkPriceResult(exchangeRate, selectionIds.Count, plan);
        }, cancellationToken);

        // S1: invalidación de cachés de producto recién después del commit.
        InvalidateAllProductCaches();
        return result!;
    }

    // ---------------------------------------------------------------------------------------------
    // Validación del contrato (400) y resolución de la selección.
    // ---------------------------------------------------------------------------------------------

    private static void ValidateBulkPriceRequest(BulkPriceUpdateRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool explicitSelection = request.ProductIds is { Count: > 0 };
        if (explicitSelection && request.SelectAllFiltered)
        {
            throw new ArgumentException("No se pueden combinar ProductIds con SelectAllFiltered: elija un único modo de selección.");
        }

        if (!explicitSelection && !request.SelectAllFiltered)
        {
            throw new ArgumentException("La solicitud no especifica los productos a actualizar: envíe ProductIds o active SelectAllFiltered.");
        }

        if (explicitSelection && request.ProductIds!.Where(id => id > 0).Distinct().Count() > MaxBulkPriceSelectionSize)
        {
            throw new ArgumentException($"La selección excede el máximo permitido ({MaxBulkPriceSelectionSize} productos).");
        }

        if (request.Operations == null || request.Operations.Count == 0)
        {
            throw new ArgumentException("La solicitud no contiene operaciones de precio.");
        }

        if (request.Operations.Count > 5 || request.Operations.Select(op => op.Field).Distinct().Count() != request.Operations.Count)
        {
            throw new ArgumentException("Solo se permite una operación por campo en la edición masiva de precios.");
        }

        foreach (var operation in request.Operations)
        {
            if (!Enum.IsDefined(operation.Field) || !Enum.IsDefined(operation.Mode))
            {
                throw new ArgumentException("La operación contiene un campo o un modo no reconocido.");
            }

            if (operation.Value < 0m)
            {
                throw new ArgumentException("El valor de cada operación debe ser mayor o igual a cero.");
            }
        }

        // 8.182-T1 (S5): contrato de la política de redondeo y de los pisos de margen por lote.
        if (!Enum.IsDefined(request.RetailRoundingRule) || !Enum.IsDefined(request.WholesaleRoundingRule))
        {
            throw new ArgumentException("La política de redondeo contiene una regla no reconocida.");
        }

        if (request.RetailMarginFloorPct < 0m || request.WholesaleMarginFloorPct < 0m)
        {
            throw new ArgumentException("El piso de margen no puede ser negativo.");
        }
    }

    private async Task<List<int>> ResolveBulkPriceSelectionIdsAsync(BulkPriceUpdateRequestDto request, CancellationToken cancellationToken)
    {
        if (request.ProductIds is { Count: > 0 })
        {
            var ids = request.ProductIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
            var existingIds = await _context.Products
                .AsNoTracking()
                .Where(p => ids.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            var missing = ids.Except(existingIds).ToList();
            if (missing.Count > 0)
            {
                throw new KeyNotFoundException($"Producto(s) con ID {string.Join(", ", missing)} no encontrado(s).");
            }

            return ids;
        }

        // S1: "todos los filtrados" reutiliza la MISMA semántica del listado del catálogo
        // (GetProductsPagedAsync): filtro de texto, estado y subárbol de categoría.
        var page = await GetProductsPagedAsync(
            filter: request.Filter?.SearchText,
            page: 1,
            pageSize: MaxBulkPriceSelectionSize,
            statusFilter: request.Filter?.StatusFilter,
            sortBy: null,
            isDescending: false,
            token: cancellationToken,
            categoryId: request.Filter?.CategoryId);

        if (page.TotalCount > MaxBulkPriceSelectionSize)
        {
            throw new ArgumentException($"La selección por filtro excede el máximo permitido ({MaxBulkPriceSelectionSize} productos).");
        }

        return page.Items.Select(p => p.Id).OrderBy(id => id).ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Motor compartido de cálculo (S2/S3/S4).
    // ---------------------------------------------------------------------------------------------

    private async Task<List<BulkPricePlanItem>> BuildBulkPricePlanAsync(IReadOnlyList<Product> products, BulkPriceUpdateRequestDto request, decimal exchangeRate, CancellationToken cancellationToken)
    {
        var parentIds = products
            .Where(p => p.ParentProductId.HasValue)
            .Select(p => p.ParentProductId!.Value)
            .Distinct()
            .ToList();

        var parentsById = parentIds.Count == 0
            ? new Dictionary<int, Product>()
            : await _context.Products
                .AsNoTracking()
                .Where(p => parentIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

        var plan = new List<BulkPricePlanItem>(products.Count);
        foreach (var product in products)
        {
            parentsById.TryGetValue(product.ParentProductId ?? 0, out var parent);
            plan.Add(ComputeBulkPricePlanItem(product, parent, request, exchangeRate));
        }

        return plan;
    }

    private static BulkPricePlanItem ComputeBulkPricePlanItem(Product product, Product? parent, BulkPriceUpdateRequestDto request, decimal exchangeRate)
    {
        var operations = request.Operations;
        var scratch = CreateBulkPriceScratch(product);

        // S3: un agrupador con precios independientes no posee precios propios (invariante en cero).
        if (product.IsGroupHeader && product.HasIndependentPricing)
        {
            return BulkPriceSkipped(product, scratch, "Grupo con precios independientes: los precios se editan en cada variante.");
        }

        // S3: una variante de un grupo con precios heredados recibe los precios del padre (el update
        // individual los sobrescribe con CopyPricingFromParent): se omite con motivo legible.
        if (product.ParentProductId.HasValue && parent != null && !parent.IsDeleted && !parent.HasIndependentPricing)
        {
            return BulkPriceSkipped(product, scratch, "Variante con precios heredados del grupo: los precios se editan en el producto padre.");
        }

        // Las operaciones al mayor sobre un producto sin precio al mayor habilitado no surtirían
        // efecto: la rutina vigente las sobrescribe con los valores del detal.
        if (!product.HasWholesale && operations.Any(op => op.Field is BulkPriceField.WholesalePrice or BulkPriceField.WholesaleMargin))
        {
            return BulkPriceSkipped(product, scratch, "El producto no tiene habilitado el precio al mayor.");
        }

        foreach (var operation in operations)
        {
            var raw = ComputeBulkPriceOperationValue(scratch, request, operation);
            WriteBulkPriceField(scratch, operation.Field, RoundBulkPriceField(operation.Field, raw));
        }

        // Los negativos se detectan ANTES de las rutinas vigentes: su derivación
        // precio=costo×(1+margen) reemplazaría (enmascararía) un resultado negativo silenciosamente.
        var negativeField = FindNegativeBulkPriceField(scratch);
        if (negativeField != null)
        {
            return BulkPriceError(product, scratch, $"El valor resultante no puede ser negativo ({negativeField}).");
        }

        // S2 "precio fijo manda": el margen recalcula precio=costo×(1+margen) con techo, salvo que
        // la misma solicitud fije el precio de ese canal.
        bool retailPriceSet = operations.Any(op => op.Field == BulkPriceField.RetailPrice);
        bool wholesalePriceSet = operations.Any(op => op.Field == BulkPriceField.WholesalePrice);
        if (!retailPriceSet && scratch.CostPriceUSD > 0m && operations.Any(op => op.Field == BulkPriceField.RetailMargin))
        {
            scratch.PriceRetailUSD = PricingCalculator.RoundPriceUp(scratch.CostPriceUSD * (1m + scratch.ProfitMarginRetail / 100m));
        }
        if (!wholesalePriceSet && scratch.CostPriceUSD > 0m && operations.Any(op => op.Field == BulkPriceField.WholesaleMargin))
        {
            scratch.PriceWholesaleUSD = PricingCalculator.RoundPriceUp(scratch.CostPriceUSD * (1m + scratch.ProfitMarginWholesale / 100m));
        }

        // -----------------------------------------------------------------------------------------
        // 8.182-T1 (S4): interceptor — redondeo por canal (S1/S2) y salvaguarda Detal/Mayor (S3)
        // ANTES de la validación vigente: `ValidateAndCalculateProductPrices` sigue siendo la
        // última palabra (negativos / mayor≤detal / cap). Con ExactMath todo queda como en 8.181.
        // -----------------------------------------------------------------------------------------
        bool retailRoundingActive = request.RetailRoundingRule != RoundingRuleType.ExactMath;
        bool wholesaleRoundingActive = request.WholesaleRoundingRule != RoundingRuleType.ExactMath;
        decimal? retailPreRounding = null;
        decimal? retailRoundingDelta = null;
        decimal? retailCashFriendlyAfterBsS = null;
        decimal? wholesalePreRounding = null;
        decimal? wholesaleRoundingDelta = null;
        decimal? wholesaleBsSBefore = null;
        decimal? wholesaleBsSAfter = null;
        bool retailUsdRoundingApplied = false;
        bool wholesaleUsdRoundingApplied = false;

        // Redondeo USD del canal al detal (UsdPsychological). Spec V0.15: la proyección Bs.S vive en
        // una etapa aparte gobernada por request.CashBsRoundingRule; la guarda !IsVesCashFriendly es
        // defensiva — un caller viejo que envíe VES en la regla del canal ya no proyecta nada.
        if (retailRoundingActive && !IsVesCashFriendly(request.RetailRoundingRule))
        {
            decimal retailUsd = EffectiveBulkRetailUsd(scratch);
            if (retailUsd > 0m)
            {
                var retailRounding = RoundingPolicy.Round(
                    retailUsd,
                    request.RetailRoundingRule,
                    scratch.CostPriceUSD,
                    request.RetailMarginFloorPct,
                    request.EnforceMinimumMarginFloor);
                if (retailRounding.RoundedValue != retailUsd)
                {
                    scratch.PriceRetailUSD = retailRounding.RoundedValue;
                    retailPreRounding = retailRounding.MathematicalValue;
                    retailRoundingDelta = retailRounding.RoundingDelta;
                    retailUsdRoundingApplied = true;
                }
            }
        }

        // Redondeo USD del canal al mayor (UsdPsychological); misma guarda defensiva que el detal.
        // El Bs.S informativo del mayor se calcula en la etapa de proyección cash (más abajo).
        if (wholesaleRoundingActive && !IsVesCashFriendly(request.WholesaleRoundingRule) && scratch.HasWholesale && scratch.PriceWholesaleUSD > 0m)
        {
            var wholesaleRounding = RoundingPolicy.Round(
                scratch.PriceWholesaleUSD,
                request.WholesaleRoundingRule,
                scratch.CostPriceUSD,
                request.WholesaleMarginFloorPct,
                request.EnforceMinimumMarginFloor);
            if (wholesaleRounding.RoundedValue != scratch.PriceWholesaleUSD)
            {
                scratch.PriceWholesaleUSD = wholesaleRounding.RoundedValue;
                wholesalePreRounding = wholesaleRounding.MathematicalValue;
                wholesaleRoundingDelta = wholesaleRounding.RoundingDelta;
                wholesaleUsdRoundingApplied = true;
            }
        }

        // S3: si tras el redondeo el Mayor queda ≥ al Detal, se baja al escalón inmediato inferior de
        // su regla, siempre que quede por encima del costo; si no es posible, la fila bloquea el apply
        // (all-or-nothing). Solo interviene cuando una regla USD movió realmente un precio; si no,
        // rige la validación 8.181 sin cambios.
        if ((retailUsdRoundingApplied || wholesaleUsdRoundingApplied) && scratch.HasWholesale)
        {
            decimal retailUsd = EffectiveBulkRetailUsd(scratch);
            if (retailUsd > 0m && scratch.PriceWholesaleUSD > 0m && scratch.PriceWholesaleUSD >= retailUsd)
            {
                decimal wholesaleBeforeSalvage = scratch.PriceWholesaleUSD;
                decimal? lowered = RoundingPolicy.FindLowerStep(wholesaleBeforeSalvage, request.WholesaleRoundingRule);
                if (lowered is > 0m && (scratch.CostPriceUSD <= 0m || lowered.Value > scratch.CostPriceUSD))
                {
                    scratch.PriceWholesaleUSD = lowered.Value;
                    wholesaleUsdRoundingApplied = true;
                    if (!IsVesCashFriendly(request.WholesaleRoundingRule))
                    {
                        // El ajuste de la salvaguarda también se reporta como delta del canal.
                        wholesalePreRounding ??= wholesaleBeforeSalvage;
                        wholesaleRoundingDelta = scratch.PriceWholesaleUSD - wholesalePreRounding.Value;
                    }
                }
                else
                {
                    return BulkPriceError(product, scratch,
                        $"El precio al mayor resultante (${wholesaleBeforeSalvage:F2}) no puede ser mayor o igual al detal (${retailUsd:F2}) ni pudo ajustarse al escalón inferior sin bajar del costo (${scratch.CostPriceUSD:F2}).");
                }
            }
        }

        // 8.183 (R3): el redondeo NUNCA pisa el margen persistido. El margen de la entidad es siempre
        // el objetivo/operado (op del lote o del revisor); el efectivo se informa SOLO en la fila del
        // preview (EffectiveRetailMarginPct/EffectiveWholesaleMarginPct, más abajo). Antes de 8.183
        // este punto sobrescribía el margen con el efectivo inflado por el redondeo y la siguiente
        // iteración lo heredaba: la raíz del Efecto Trinquete (inflación acumulativa).

        try
        {
            // MISMAS rutinas del update individual (S2): precedencias, derivaciones y validaciones.
            ValidateAndCalculateProductPrices(scratch);
        }
        catch (ArgumentException ex)
        {
            return BulkPriceError(product, scratch, ex.Message);
        }

        var capViolation = FindBulkPriceCapViolation(scratch);
        if (capViolation != null)
        {
            return BulkPriceError(product, scratch, capViolation);
        }

        decimal beforeRetail = EffectiveBulkRetailUsd(product);
        decimal afterRetail = EffectiveBulkRetailUsd(scratch);
        decimal newPriceBsS = product.PriceBsS;
        if (afterRetail != beforeRetail && exchangeRate > 0m)
        {
            // Bs.S canónico por techo con la tasa del día (8.104). Sin tasa configurada se conserva
            // el Bs.S previo (a diferencia de create/update, aquí no existe un Bs.S de respaldo del cliente).
            newPriceBsS = PricingCalculator.ToBsSCeiling(afterRetail, exchangeRate);
        }

        // 8.183 (R1) + Spec V0.15: la proyección Bs.S del detal NO persiste nada — el Bs.S almacenado
        // es SIEMPRE el canónico 8.104 calculado arriba (el USD y el margen no cambian). La capa cash
        // (request.CashBsRoundingRule) se evalúa en caliente con la tasa del día y solo se informa en
        // CashFriendlyAfterBsS del cambio (nunca participa del estado ni de la persistencia).
        if (IsVesCashFriendly(request.CashBsRoundingRule))
        {
            var retailCashProjection = RoundingPolicy.ProjectCashFriendlyBsS(
                afterRetail,
                exchangeRate,
                request.CashBsRoundingRule,
                scratch.CostPriceUSD,
                request.RetailMarginFloorPct,
                request.EnforceMinimumMarginFloor);
            if (retailCashProjection is { } cashProjection)
            {
                retailCashFriendlyAfterBsS = cashProjection.RoundedValue;
                retailPreRounding = cashProjection.MathematicalValue;
                retailRoundingDelta = cashProjection.RoundingDelta;
            }
        }

        // 8.182-T1 (S4) / 8.183 (R1) + Spec V0.15: proyección cash del canal al mayor con la capa
        // request.CashBsRoundingRule (la entidad no tiene columna Bs.S propia para ese canal);
        // informativa y nunca persistida, solo acompaña a un cambio real del precio USD.
        if (IsVesCashFriendly(request.CashBsRoundingRule)
            && scratch.HasWholesale && scratch.PriceWholesaleUSD > 0m
            && scratch.PriceWholesaleUSD != product.PriceWholesaleUSD && exchangeRate > 0m)
        {
            var wholesaleVesRounding = RoundingPolicy.Round(
                scratch.PriceWholesaleUSD * exchangeRate,
                request.CashBsRoundingRule,
                scratch.CostPriceUSD * exchangeRate,
                request.WholesaleMarginFloorPct,
                request.EnforceMinimumMarginFloor);
            wholesaleBsSBefore = PricingCalculator.ToBsSCeiling(product.PriceWholesaleUSD, exchangeRate);
            wholesaleBsSAfter = wholesaleVesRounding.RoundedValue;
            wholesalePreRounding = wholesaleVesRounding.MathematicalValue;
            wholesaleRoundingDelta = wholesaleVesRounding.RoundingDelta;
        }

        bool stateChanged = BulkPriceStateChanged(product, scratch, newPriceBsS);
        var row = CreateBulkPriceRow(product);
        row.Changes = BuildBulkPriceChanges(product, scratch, newPriceBsS, new BulkPriceRoundingInfo(
            retailPreRounding,
            retailRoundingDelta,
            retailCashFriendlyAfterBsS,
            wholesalePreRounding,
            wholesaleRoundingDelta,
            wholesaleBsSBefore,
            wholesaleBsSAfter));

        // S5: margen efectivo (precio final vs costo) del canal, solo cuando el precio USD cambió.
        row.EffectiveRetailMarginPct = afterRetail != beforeRetail && scratch.CostPriceUSD > 0m
            ? CalculateEffectiveMarginPct(afterRetail, scratch.CostPriceUSD)
            : null;
        row.EffectiveWholesaleMarginPct = scratch.HasWholesale && scratch.PriceWholesaleUSD != product.PriceWholesaleUSD && scratch.CostPriceUSD > 0m
            ? CalculateEffectiveMarginPct(scratch.PriceWholesaleUSD, scratch.CostPriceUSD)
            : null;

        if (!stateChanged)
        {
            row.SkipReason = "Sin cambios: los valores resultantes son iguales a los actuales.";
        }

        return new BulkPricePlanItem(product, scratch, row, stateChanged, newPriceBsS);
    }

    // ---------------------------------------------------------------------------------------------
    // Persistencia y propagación a variantes (S3).
    // ---------------------------------------------------------------------------------------------

    private static void ApplyBulkPriceState(BulkPricePlanItem item)
    {
        var product = item.Product;
        var scratch = item.Scratch;
        product.PriceRetailUSD = scratch.PriceRetailUSD;
        product.PriceWholesaleUSD = scratch.PriceWholesaleUSD;
        product.CostPriceUSD = scratch.CostPriceUSD;
        product.ProfitMarginRetail = scratch.ProfitMarginRetail;
        product.ProfitMarginWholesale = scratch.ProfitMarginWholesale;
        product.HasWholesale = scratch.HasWholesale;
        product.MinWholesaleQuantity = scratch.MinWholesaleQuantity;
        product.PriceUSD = scratch.PriceUSD;
        product.Cost = scratch.Cost;
        product.ProfitPercentage = scratch.ProfitPercentage;
        // S2: LastConversionRate no se escribe (igual que create/update actuales).
        product.PriceBsS = item.NewPriceBsS;
        product.UpdatedAt = DateTime.UtcNow;
    }

    private async Task PropagateBulkPriceGroupsAsync(IReadOnlyList<BulkPricePlanItem> plan, decimal exchangeRate, CancellationToken cancellationToken)
    {
        var groupItems = plan
            .Where(item => item.HasChanges && item.Product.IsGroupHeader && !item.Product.HasIndependentPricing)
            .ToList();
        if (groupItems.Count == 0)
        {
            return;
        }

        var groupIds = groupItems.Select(item => item.Product.Id).ToList();
        // Camino rastreado (a diferencia del ExecuteUpdate del update individual, que evade xmin):
        // un conflicto de concurrencia en un hijo revierte también la actualización del padre.
        var children = await _context.Products
            .Where(p => p.ParentProductId.HasValue && groupIds.Contains(p.ParentProductId.Value) && !p.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var item in groupItems)
        {
            var header = item.Product;
            foreach (var child in children.Where(c => c.ParentProductId == header.Id))
            {
                bool retailPriceChanged = EffectiveBulkRetailUsd(child) != EffectiveBulkRetailUsd(header);
                child.PriceRetailUSD = header.PriceRetailUSD;
                child.PriceWholesaleUSD = header.PriceWholesaleUSD;
                child.CostPriceUSD = header.CostPriceUSD;
                child.ProfitMarginRetail = header.ProfitMarginRetail;
                child.ProfitMarginWholesale = header.ProfitMarginWholesale;
                child.HasWholesale = header.HasWholesale;
                child.MinWholesaleQuantity = header.MinWholesaleQuantity;
                child.PriceUSD = header.PriceRetailUSD;
                child.Cost = header.CostPriceUSD;
                child.ProfitPercentage = header.ProfitMarginRetail;

                // S3: todo hijo cuyo precio USD cambie recalcula su Bs.S canónico (el update
                // individual omitía este recálculo; el bulk lo corrige).
                if (retailPriceChanged && exchangeRate > 0m)
                {
                    child.PriceBsS = PricingCalculator.ToBsSCeiling(EffectiveBulkRetailUsd(header), exchangeRate);
                }

                child.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers del motor.
    // ---------------------------------------------------------------------------------------------

    private static Product CreateBulkPriceScratch(Product product) => new()
    {
        Id = product.Id,
        SKU = product.SKU,
        Name = product.Name,
        IsGroupHeader = product.IsGroupHeader,
        HasIndependentPricing = product.HasIndependentPricing,
        IsCashAdvance = product.IsCashAdvance,
        CostPriceUSD = product.CostPriceUSD,
        Cost = product.Cost,
        ProfitMarginRetail = product.ProfitMarginRetail,
        ProfitPercentage = product.ProfitPercentage,
        PriceRetailUSD = product.PriceRetailUSD,
        PriceUSD = product.PriceUSD,
        PriceWholesaleUSD = product.PriceWholesaleUSD,
        ProfitMarginWholesale = product.ProfitMarginWholesale,
        HasWholesale = product.HasWholesale,
        MinWholesaleQuantity = product.MinWholesaleQuantity,
        PriceBsS = product.PriceBsS
    };

    private static decimal EffectiveBulkRetailUsd(Product product) =>
        product.PriceRetailUSD > 0m ? product.PriceRetailUSD : product.PriceUSD;

    private static decimal ReadBulkPriceField(Product product, BulkPriceField field) => field switch
    {
        BulkPriceField.RetailPrice => EffectiveBulkRetailUsd(product),
        BulkPriceField.WholesalePrice => product.PriceWholesaleUSD,
        BulkPriceField.RetailMargin => product.ProfitMarginRetail,
        BulkPriceField.WholesaleMargin => product.ProfitMarginWholesale,
        _ => product.CostPriceUSD
    };

    private static void WriteBulkPriceField(Product product, BulkPriceField field, decimal value)
    {
        switch (field)
        {
            case BulkPriceField.RetailPrice:
                product.PriceRetailUSD = value;
                break;
            case BulkPriceField.WholesalePrice:
                product.PriceWholesaleUSD = value;
                break;
            case BulkPriceField.RetailMargin:
                product.ProfitMarginRetail = value;
                break;
            case BulkPriceField.WholesaleMargin:
                product.ProfitMarginWholesale = value;
                break;
            default:
                product.CostPriceUSD = value;
                break;
        }
    }

    private static decimal RoundBulkPriceField(BulkPriceField field, decimal value) => field switch
    {
        // Los precios calculados nunca redondean hacia abajo (RoundPriceUp, 8.104); márgenes y costo
        // usan la precisión digital (2 decimales AwayFromZero) igual que el update individual.
        BulkPriceField.RetailPrice or BulkPriceField.WholesalePrice => PricingCalculator.RoundPriceUp(value),
        _ => PricingCalculator.RoundToDigital(value)
    };

    // Spec V0.15: valor resultante de UNA operación del lote sobre el scratch.
    // Los campos de margen (Detal/Mayor) operan en PUNTOS PORCENTUALES aditivos: aumentar/reducir
    // suma/resta Δ al margen actual (10 + 10 pp = 20; antes el modo % era multiplicativo y daba 11).
    // Fijar valor sigue siendo Δ exacto. Precio Detal/Mayor y Costo conservan intacta la variación
    // porcentual multiplicativa (o la suma/resta fija en USD) del lote 8.181.
    private static decimal ComputeBulkPriceOperationValue(Product scratch, BulkPriceUpdateRequestDto request, BulkPriceOperationDto operation)
    {
        decimal current = ReadBulkPriceField(scratch, operation.Field);
        bool isMargin = operation.Field is BulkPriceField.RetailMargin or BulkPriceField.WholesaleMargin;

        decimal raw = operation.Mode switch
        {
            BulkPriceMode.SetFixed => operation.Value,
            BulkPriceMode.IncreasePercent when isMargin => current + operation.Value,
            BulkPriceMode.DecreasePercent when isMargin => current - operation.Value,
            BulkPriceMode.IncreasePercent => current * (1m + operation.Value / 100m),
            BulkPriceMode.DecreasePercent => current * (1m - operation.Value / 100m),
            _ => current
        };

        // Salvaguarda SOLO en REDUCCIONES de margen (incrementar/fijar no cambian): el margen nunca
        // queda bajo el piso del canal con el toggle activo (piso null/≤0 ⇒ cota 0) ni bajo 0 sin
        // él. Clamp literal (max): si el margen actual YA está bajo el piso, una reducción lo SUBE
        // al piso — no existe reducción efectiva por debajo de esa cota.
        if (isMargin && operation.Mode == BulkPriceMode.DecreasePercent)
        {
            decimal? channelFloor = operation.Field == BulkPriceField.RetailMargin
                ? request.RetailMarginFloorPct
                : request.WholesaleMarginFloorPct;
            decimal floor = request.EnforceMinimumMarginFloor ? channelFloor ?? 0m : 0m;
            raw = Math.Max(raw, floor);
        }

        return raw;
    }

    private static string? FindNegativeBulkPriceField(Product scratch)
    {
        if (scratch.PriceRetailUSD < 0m) return "precio al detal";
        if (scratch.PriceWholesaleUSD < 0m) return "precio al mayor";
        if (scratch.ProfitMarginRetail < 0m) return "margen al detal";
        if (scratch.ProfitMarginWholesale < 0m) return "margen al mayor";
        if (scratch.CostPriceUSD < 0m) return "costo";
        return null;
    }

    private static string? FindBulkPriceCapViolation(Product scratch)
    {
        if (scratch.PriceRetailUSD > MaxBulkPriceAmount)
        {
            return $"El precio al detal resultante (${scratch.PriceRetailUSD:F2}) excede el máximo permitido (${MaxBulkPriceAmount:F2}).";
        }
        if (scratch.PriceWholesaleUSD > MaxBulkPriceAmount)
        {
            return $"El precio al mayor resultante (${scratch.PriceWholesaleUSD:F2}) excede el máximo permitido (${MaxBulkPriceAmount:F2}).";
        }
        if (scratch.CostPriceUSD > MaxBulkPriceAmount)
        {
            return $"El costo resultante (${scratch.CostPriceUSD:F2}) excede el máximo permitido (${MaxBulkPriceAmount:F2}).";
        }
        if (scratch.ProfitMarginRetail > MaxBulkPriceMargin)
        {
            return $"El margen al detal resultante ({scratch.ProfitMarginRetail:F2}%) excede el máximo permitido ({MaxBulkPriceMargin:F2}%).";
        }
        if (scratch.ProfitMarginWholesale > MaxBulkPriceMargin)
        {
            return $"El margen al mayor resultante ({scratch.ProfitMarginWholesale:F2}%) excede el máximo permitido ({MaxBulkPriceMargin:F2}%).";
        }
        return null;
    }

    private static bool BulkPriceStateChanged(Product before, Product after, decimal newPriceBsS) =>
        before.PriceRetailUSD != after.PriceRetailUSD
        || before.PriceWholesaleUSD != after.PriceWholesaleUSD
        || before.CostPriceUSD != after.CostPriceUSD
        || before.ProfitMarginRetail != after.ProfitMarginRetail
        || before.ProfitMarginWholesale != after.ProfitMarginWholesale
        || before.HasWholesale != after.HasWholesale
        || before.MinWholesaleQuantity != after.MinWholesaleQuantity
        || before.PriceUSD != after.PriceUSD
        || before.Cost != after.Cost
        || before.ProfitPercentage != after.ProfitPercentage
        || before.PriceBsS != newPriceBsS;

    private static List<BulkPriceFieldChangeDto> BuildBulkPriceChanges(Product before, Product after, decimal newPriceBsS, BulkPriceRoundingInfo rounding)
    {
        var changes = new List<BulkPriceFieldChangeDto>();
        foreach (var field in Enum.GetValues<BulkPriceField>())
        {
            // Los campos al mayor de un producto sin mayor habilitado son espejos del detal: la
            // rutina los replica, no son cambios accionables para el operador.
            if (!before.HasWholesale && field is BulkPriceField.WholesalePrice or BulkPriceField.WholesaleMargin)
            {
                continue;
            }

            decimal beforeValue = ReadBulkPriceField(before, field);
            decimal afterValue = ReadBulkPriceField(after, field);

            decimal? beforeBsS = null;
            decimal? afterBsS = null;
            decimal? preRounding = null;
            decimal? roundingDelta = null;
            decimal? cashFriendlyAfterBsS = null;

            if (field == BulkPriceField.RetailPrice)
            {
                // 8.183 (R1): el Bs.S del detal es SIEMPRE el canónico persistido; la proyección de
                // efectivo (VES) viaja aparte en CashFriendlyAfterBsS y se informa aunque el estado
                // persistido no cambie (nunca participa de HasChanges ni se escribe).
                beforeBsS = before.PriceBsS;
                afterBsS = newPriceBsS;
                preRounding = rounding.RetailPreRoundingValue;
                roundingDelta = rounding.RetailRoundingDelta;
                cashFriendlyAfterBsS = rounding.RetailCashFriendlyAfterBsS;
                if (beforeValue == afterValue && beforeBsS == afterBsS && !cashFriendlyAfterBsS.HasValue)
                {
                    continue;
                }
            }
            else if (field == BulkPriceField.WholesalePrice)
            {
                if (beforeValue == afterValue)
                {
                    continue;
                }

                // 8.182-T1: Bs.S informativo del mayor (solo con regla VES activa sobre el canal).
                // 8.183 (R1): ese Bs.S equivalente ES la proyección de efectivo del canal al mayor.
                beforeBsS = rounding.WholesaleBsSBefore;
                afterBsS = rounding.WholesaleBsSAfter;
                preRounding = rounding.WholesalePreRoundingValue;
                roundingDelta = rounding.WholesaleRoundingDelta;
                cashFriendlyAfterBsS = rounding.WholesaleBsSAfter;
            }
            else if (beforeValue == afterValue)
            {
                continue;
            }

            changes.Add(new BulkPriceFieldChangeDto
            {
                Field = field,
                BeforeValue = beforeValue,
                AfterValue = afterValue,
                BeforeBsS = beforeBsS,
                AfterBsS = afterBsS,
                PreRoundingValue = preRounding,
                RoundingDelta = roundingDelta,
                CashFriendlyAfterBsS = cashFriendlyAfterBsS
            });
        }

        return changes;
    }

    private static bool IsVesCashFriendly(RoundingRuleType rule) =>
        rule is RoundingRuleType.VesCashFriendlyInteger or RoundingRuleType.VesCashFriendlyStep5 or RoundingRuleType.VesCashFriendlyStep10;

    private static decimal CalculateEffectiveMarginPct(decimal price, decimal cost) =>
        PricingCalculator.RoundToDigital((price - cost) / cost * 100m);

    // 8.182-T1: traza del redondeo por fila (unidades: USD para UsdPsychological; Bs.S para VES).
    // 8.183 (R1): RetailCashFriendlyAfterBsS es la proyección de efectivo del detal (nunca
    // persistida); en el canal al mayor la proyección viaja como Bs.S informativo (WholesaleBsSAfter).
    private sealed record BulkPriceRoundingInfo(
        decimal? RetailPreRoundingValue,
        decimal? RetailRoundingDelta,
        decimal? RetailCashFriendlyAfterBsS,
        decimal? WholesalePreRoundingValue,
        decimal? WholesaleRoundingDelta,
        decimal? WholesaleBsSBefore,
        decimal? WholesaleBsSAfter);

    private static BulkPricePreviewRowDto CreateBulkPriceRow(Product product) => new()
    {
        ProductId = product.Id,
        SKU = product.SKU,
        Name = product.Name,
        IsGroupHeader = product.IsGroupHeader,
        HasIndependentPricing = product.HasIndependentPricing
    };

    private static BulkPricePlanItem BulkPriceSkipped(Product product, Product scratch, string reason)
    {
        var row = CreateBulkPriceRow(product);
        row.SkipReason = reason;
        return new BulkPricePlanItem(product, scratch, row, false, product.PriceBsS);
    }

    private static BulkPricePlanItem BulkPriceError(Product product, Product scratch, string reason)
    {
        var row = CreateBulkPriceRow(product);
        row.SkipReason = reason;
        row.IsError = true;
        return new BulkPricePlanItem(product, scratch, row, false, product.PriceBsS);
    }

    private static BulkPriceUpdateResultDto BuildBulkPriceResult(decimal exchangeRate, int selectedCount, IReadOnlyList<BulkPricePlanItem> plan)
    {
        var rows = plan.Select(item => item.Row).ToList();
        int updatedCount = plan.Count(item => item.HasChanges && item.Row.SkipReason == null);
        return new BulkPriceUpdateResultDto
        {
            ExchangeRate = exchangeRate,
            SelectedCount = selectedCount,
            UpdatedCount = updatedCount,
            SkippedCount = rows.Count - updatedCount,
            Rows = rows
        };
    }

    private sealed record BulkPricePlanItem(Product Product, Product Scratch, BulkPricePreviewRowDto Row, bool HasChanges, decimal NewPriceBsS);
}
