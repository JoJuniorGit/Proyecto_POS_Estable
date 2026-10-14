import { api } from './api';

/**
 * 8.179-T3 (S4): listado plano de categorías jerárquicas para los filtros del catálogo y del POS.
 * Contrato congelado en T1: GET /api/categories → [{ id, name, parentCategoryId, depth, path }]
 * con `path` legible "A > B > C". Solo lectura: la gestión de categorías vive en Desktop.
 * @param {AbortSignal} [signal]
 * @returns {Promise<Array<{id:number, name:string, parentCategoryId:number|null, depth:number, path:string}>>}
 */
export async function getCategories(signal) {
  const data = await api.get('/api/categories', signal);
  return Array.isArray(data) ? data : [];
}

/**
 * Aplana el listado del backend para un <select>: `value` = id, etiqueta indentada por
 * profundidad (NBSP, los <option> no admiten CSS de indentación) y ruta completa para
 * desambiguar nombres repetidos entre ramas.
 */
export function toCategoryOptions(categories) {
  if (!Array.isArray(categories)) return [];
  return categories.map((category) => ({
    value: String(category.id),
    label: `${'\u00A0\u00A0'.repeat(Math.max(0, Number(category.depth) || 0))}${category.name ?? ''}`,
    path: category.path || category.name || '',
  }));
}
