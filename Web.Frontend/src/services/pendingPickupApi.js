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

/**
 * Registra las cantidades retiradas de una venta en custodia.
 * @param {number} saleId - ID de la venta
 * @param {Array<{ saleItemId: number, quantity: number }>} items - Cantidades retiradas por línea
 * @param {string|null} notes - Nota opcional del retiro
 * @param {string} idempotencyKey - Clave estable durante el intento de despacho
 * @returns {Promise<Object>} Comprobante de despacho en camelCase
 */
export async function deliverPartialPickup(saleId, items, notes, idempotencyKey) {
  if (typeof idempotencyKey !== 'string' || !idempotencyKey.trim()) {
    throw new TypeError('La clave de idempotencia es obligatoria.');
  }

  return await api.post(
    `/api/sales/${saleId}/deliveries`,
    { items, notes: notes || null },
    { headers: { 'Idempotency-Key': idempotencyKey } }
  );
}

/**
 * Descarga con la sesión autenticada la nota PDF de un retiro específico.
 * @param {number} saleId - ID de la venta
 * @param {number} deliveryId - ID del evento de despacho
 * @returns {Promise<Blob>} Nota de despacho en PDF
 */
export async function getDeliveryNoteBlob(saleId, deliveryId) {
  return await api.getBlob(`/api/sales/${saleId}/deliveries/${deliveryId}/receipt`, {
    headers: { Accept: 'application/pdf' },
  });
}
