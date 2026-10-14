import { api } from './api';

/**
 * Obtiene sugerencias de productos para la barra de búsqueda.
 * @param {string} filter - Término de búsqueda
 * @param {AbortSignal} [signal]
 * @param {number|string} [categoryId] - 8.179-T3 (S4): filtra por categoría y su subárbol; opcional.
 */
export async function getProductSuggestions(filter, signal, categoryId) {
  if (!filter || filter.trim().length === 0) return [];
  const query = encodeURIComponent(filter.trim());
  const categoryParam = categoryId ? `&categoryId=${encodeURIComponent(categoryId)}` : '';
  return await api.get(`/api/products/suggestions?filter=${query}${categoryParam}`, signal);
}

/**
 * Consulta rápida por código SKU / de barras.
 * @param {string} sku
 * @param {AbortSignal} [signal]
 */
export async function getProductBySku(sku, signal) {
  if (!sku) return null;
  return await api.get(`/api/products/quick-check/${encodeURIComponent(sku)}`, signal);
}
