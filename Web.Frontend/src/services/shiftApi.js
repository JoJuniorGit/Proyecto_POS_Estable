import { api } from './api';

/**
 * Envia el cierre ciego de turno enviando el arreglo de montos declarados
 * estrictamente en la moneda nativa de cada método de pago.
 * @param {Array<{paymentMethodId: number, paymentMethodName: string, amount: number, currency: string}>} declaredAmounts
 * @param {string} cashierName
 * @param {string} cashierCedula
 */
export async function closeShift(declaredAmounts, cashierName = '', cashierCedula = '') {
  return await api.post('/api/shifts/close', {
    cashierName,
    cashierCedula,
    declaredAmounts,
  });
}

/**
 * Obtiene el reporte Z actual para recuperación en caso de turno cerrado.
 */
export async function getCurrentShiftReport() {
  return await api.get('/api/shifts/current/report');
}

/**
 * Obtiene un reporte Z por ID de turno.
 */
export async function getShiftReportById(shiftId) {
  return await api.get(`/api/shifts/${shiftId}/report`);
}
