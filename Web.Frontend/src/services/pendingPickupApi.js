import { api } from './api';

/**
 * Obtiene la lista de facturas pendientes de retiro (mercancía en custodia).
 * @returns {Promise<Array>} Lista de objetos PendingPickupDto
 */
export async function getPendingPickups() {
  return await api.get('/api/sales/pending-pickups');
}

/**
 * Confirma la entrega física de la mercancía de un pedido en custodia.
 * @param {number} saleId - ID de la venta
 * @returns {Promise<Object>} Detalle actualizado de la venta (SaleHistoryDto)
 */
export async function confirmPickup(saleId) {
  return await api.post(`/api/sales/${saleId}/confirm-pickup`);
}
