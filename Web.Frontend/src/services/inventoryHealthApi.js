import { api } from './api';

const BULK_PRICE_FIELD_RETAIL = 0;
const BULK_PRICE_MODE_DECREASE_PERCENT = 2;

/**
 * Paso 15 (S1-S5): query string del reporte. Los valores numericos se envian sin locale para que
 * el backend no dependa del idioma del navegador.
 */
export function buildInventoryHealthQuery(filter = {}) {
  const params = new URLSearchParams();
  params.set('salesWindowDays', String(filter.salesWindowDays ?? 90));
  params.set('deadStockDays', String(filter.deadStockDays ?? 60));
  params.set('includeCategorySubtree', String(filter.includeCategorySubtree ?? true));
  params.set('sortBy', filter.sortBy ?? 'daysSinceLastSale');
  params.set('descending', String(filter.descending ?? true));
  params.set('page', String(filter.page ?? 1));
  params.set('pageSize', String(filter.pageSize ?? 20));

  if (filter.categoryId) params.set('categoryId', String(filter.categoryId));
  if (filter.searchText) params.set('searchText', filter.searchText.trim());
  if (filter.rotationClass) params.set('rotationClass', filter.rotationClass);

  return `?${params.toString()}`;
}

export async function getInventoryHealthReport(filter, signal) {
  return api.get(`/api/inventory-rotation/report${buildInventoryHealthQuery(filter)}`, signal);
}

export async function getDeadStockSelection(filter, signal) {
  return api.get(`/api/inventory-rotation/dead-stock-ids${buildInventoryHealthQuery(filter)}`, signal);
}

/**
 * Paso 15 (S5): mapeo directo al contrato del Paso 9. La rebaja se aplica solo al precio Detal
 * USD con el motor existente (redondeo, pisos y RBAC server-side).
 */
export function buildDeadStockPromotionRequest(productIds, percentage) {
  const value = Number(percentage);
  if (!Array.isArray(productIds) || productIds.length === 0) {
    throw new Error('No hay productos para promocionar.');
  }
  if (!Number.isFinite(value) || value <= 0 || value > 100) {
    throw new Error('El porcentaje de descuento debe estar entre 1 y 100.');
  }

  return {
    productIds,
    selectAllFiltered: false,
    operations: [
      {
        field: BULK_PRICE_FIELD_RETAIL,
        mode: BULK_PRICE_MODE_DECREASE_PERCENT,
        value,
      },
    ],
  };
}

export async function applyDeadStockPromotion(productIds, percentage, signal) {
  return api.post(
    '/api/products/bulk-prices/apply',
    buildDeadStockPromotionRequest(productIds, percentage),
    signal
  );
}
