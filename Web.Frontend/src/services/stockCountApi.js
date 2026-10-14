import { api } from './api';

const STOCK_COUNTS_BASE = '/api/stock-counts';

/**
 * 8.180-T2 (S5): wrappers del contrato congelado en T1 (Paso 6 — conteo físico ciego).
 * Los DTOs de detalle/scan son CIEGOS (sin existencia teórica ni costo); el descuadre vive
 * únicamente en getReview (Admin/Manager). Los problemas del backend llegan como ApiError
 * legibles a través de api.js (ProblemDetails.message).
 */

/**
 * @param {string} [status] Nombre del estado ('Open' | 'Closed' | 'Applied' | 'Cancelled').
 * @returns {Promise<Array<object>>} Sesiones ordenadas por creación descendente.
 */
export async function listStockCounts(status) {
  const query = status ? `?status=${encodeURIComponent(status)}` : '';
  const data = await api.get(`${STOCK_COUNTS_BASE}${query}`);
  return Array.isArray(data) ? data : [];
}

/**
 * @param {{name: string, scopeCategoryId?: number|null, notes?: string|null}} body
 */
export async function createStockCount(body) {
  return api.post(STOCK_COUNTS_BASE, {
    name: body?.name,
    scopeCategoryId: body?.scopeCategoryId ?? null,
    notes: body?.notes ?? null,
  });
}

/** Detalle ciego `{ session, items }` de una sesión. */
export async function getStockCount(id) {
  return api.get(`${STOCK_COUNTS_BASE}/${id}`);
}

/**
 * Escaneo por SKU/código de barras. `quantityDelta` suma al ítem existente (default +1).
 * 404 si el código no existe; 409 si la sesión no está abierta o el producto está fuera de alcance.
 */
export async function scanCode(id, code, quantityDelta = 1) {
  return api.post(`${STOCK_COUNTS_BASE}/${id}/scan`, {
    code: String(code ?? '').trim(),
    quantityDelta,
  });
}

/** Corrección absoluta de la cantidad contada (≥ 0). */
export async function setItemQuantity(id, productId, countedQuantity) {
  return api.put(`${STOCK_COUNTS_BASE}/${id}/items/${productId}`, { countedQuantity });
}

/** Quita el escaneo erróneo de un producto. */
export async function removeItem(id, productId) {
  return api.delete(`${STOCK_COUNTS_BASE}/${id}/items/${productId}`);
}

/** Descuadre de la sesión (solo Admin/Manager en backend). */
export async function getReview(id) {
  return api.get(`${STOCK_COUNTS_BASE}/${id}/review`);
}

export async function closeStockCount(id) {
  return api.post(`${STOCK_COUNTS_BASE}/${id}/close`);
}

export async function applyStockCount(id) {
  return api.post(`${STOCK_COUNTS_BASE}/${id}/apply`);
}

export async function cancelStockCount(id) {
  return api.post(`${STOCK_COUNTS_BASE}/${id}/cancel`);
}
