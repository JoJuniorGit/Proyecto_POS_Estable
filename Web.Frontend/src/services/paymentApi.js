import { api } from './api';

/**
 * Obtiene los métodos de pago activos para el checkout.
 */
export async function getActivePaymentMethods() {
  return await api.get('/api/paymentmethods/active');
}

/**
 * Obtiene todos los métodos de pago.
 */
export async function getAllPaymentMethods() {
  return await api.get('/api/paymentmethods');
}
