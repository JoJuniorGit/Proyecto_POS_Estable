import { api } from './api';

/**
 * Obtiene los métodos de pago activos para el checkout con cache-busting.
 */
export async function getActivePaymentMethods() {
  return await api.get(`/api/paymentmethods/active?_t=${Date.now()}`);
}

/**
 * Obtiene todos los métodos de pago con cache-busting.
 */
export async function getAllPaymentMethods() {
  return await api.get(`/api/paymentmethods?_t=${Date.now()}`);
}

/**
 * Crea un nuevo método de pago.
 */
export async function createPaymentMethod(dto) {
  return await api.post('/api/paymentmethods', dto);
}

/**
 * Actualiza un método de pago existente.
 */
export async function updatePaymentMethod(id, dto) {
  return await api.put(`/api/paymentmethods/${id}`, dto);
}

/**
 * Elimina o archiva un método de pago.
 */
export async function deletePaymentMethod(id) {
  return await api.delete(`/api/paymentmethods/${id}`);
}
