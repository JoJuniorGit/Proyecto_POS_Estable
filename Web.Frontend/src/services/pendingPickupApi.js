import { api } from './api';

/**
 * Obtiene la lista de facturas pendientes de retiro (mercancía en custodia).
 * @returns {Promise<Array>} Lista de objetos PendingPickupDto
 */
export async function getPendingPickups() {
  return await api.get('/api/sales/pending-pickups');
}

/**
 * 8.14-N1: página de retiros pendientes con totalCount (paginación de UI "ver más").
 * @returns {Promise<{items: Array, totalCount: number}>}
 */
export async function getPendingPickupsPage({ limit = 200, offset = 0 } = {}) {
  const { data, totalCount } = await api.getWithMeta(`/api/sales/pending-pickups?limit=${limit}&offset=${offset}`);
  return { items: Array.isArray(data) ? data : [], totalCount };
}

/**
 * Confirma la entrega física de la mercancía de un pedido en custodia.
 * @param {number} saleId - ID de la venta
 * @returns {Promise<Object>} Detalle actualizado de la venta (SaleHistoryDto)
 */
export async function confirmPickup(saleId) {
  return await api.post(`/api/sales/${saleId}/confirm-pickup`);
}
