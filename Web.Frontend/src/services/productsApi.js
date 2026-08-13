import { api } from './api';

/**
 * Obtiene sugerencias de productos para la barra de búsqueda.
 * @param {string} filter - Término de búsqueda
 * @param {AbortSignal} [signal]
 */
export async function getProductSuggestions(filter, signal) {
  if (!filter || filter.trim().length === 0) return [];
  const query = encodeURIComponent(filter.trim());
  return await api.get(`/api/products/suggestions?filter=${query}`, signal);
}

/**
 * Consulta rápida por código SKU / de barras.
 * @param {string} sku
 */
export async function getProductBySku(sku) {
  if (!sku) return null;
  return await api.get(`/api/products/quick-check/${encodeURIComponent(sku)}`);
}
