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

/**
 * WU3: página de productos activos para selectores reutilizables. Soporta búsqueda por
 * nombre/SKU y filtro por categoría (subárbol) usando el endpoint paginado del catálogo.
 * @param {{page?: number, pageSize?: number, filter?: string, categoryId?: number|string}} params
 * @param {AbortSignal} [signal]
 */
export async function getProductsPaged({ page = 1, pageSize = 50, filter = '', categoryId = '' } = {}, signal) {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    statusFilter: 'active',
    sortBy: 'name',
  });
  if (filter && filter.trim().length > 0) params.set('filter', filter.trim());
  if (categoryId) params.set('categoryId', String(categoryId));
  return await api.get(`/api/products?${params.toString()}`, signal);
}
