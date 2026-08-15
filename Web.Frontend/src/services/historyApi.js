import { api } from './api';

/**
 * Obtiene el historial de ventas paginado.
 * @param {number} page
 * @param {number} pageSize
 * @param {string} [startDate]
 * @param {string} [endDate]
 */
export async function getSalesHistory(page = 1, pageSize = 20, startDate, endDate, search) {
  let query = `/api/sales/history?page=${page}&pageSize=${pageSize}`;
  if (startDate) query += `&startDate=${encodeURIComponent(startDate)}`;
  if (endDate) query += `&endDate=${encodeURIComponent(endDate)}`;
  if (search) query += `&search=${encodeURIComponent(search)}`;
  return await api.get(query);
}

/**
 * Obtiene el detalle completo de una venta pasada.
 * @param {number} saleId
 */
export async function getSaleHistoryDetail(saleId) {
  return await api.get(`/api/sales/${saleId}/history-detail`);
}
