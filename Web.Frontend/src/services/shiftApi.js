import { api } from './api';

const USD_RECONCILIATION_ROLES = Object.freeze(['Admin', 'Manager']);

/**
 * Residual 8.187-8.188: solo Admin/Manager ven el esperado y la diferencia USD del cierre.
 * Es gating de presentación; el servidor aplica la regla blind 8.153 y anula esos campos.
 */
export function canViewUsdReconciliation(role) {
  return USD_RECONCILIATION_ROLES.includes(role);
}

/**
 * Valida el conteo USD opcional (>= 0). Vacío es válido: conserva el comportamiento previo.
 * @returns {string|null} error legible o null.
 */
export function validateActualClosingBalanceUsd(rawValue) {
  if (rawValue === null || rawValue === undefined) return null;
  if (typeof rawValue === 'string' && rawValue.trim().length === 0) return null;

  const amount = Number(rawValue);
  if (!Number.isFinite(amount) || amount < 0) {
    return 'Ingrese un conteo válido de efectivo USD (mayor o igual a cero) o déjelo vacío.';
  }

  return null;
}

/**
 * Payload del cierre diario: el conteo USD solo viaja cuando fue informado. Omitirlo mantiene
 * intacto el contrato previo (retrocompatible con cierres sin arqueo USD).
 */
export function buildCloseShiftPayload(declaredAmounts, actualClosingBalanceUsd) {
  const payload = { declaredAmounts };

  if (validateActualClosingBalanceUsd(actualClosingBalanceUsd) === null
      && actualClosingBalanceUsd !== null
      && actualClosingBalanceUsd !== undefined
      && !(typeof actualClosingBalanceUsd === 'string' && actualClosingBalanceUsd.trim().length === 0)) {
    payload.actualClosingBalanceUsd = Number(actualClosingBalanceUsd);
  }

  return payload;
}

/**
 * Envia el cierre ciego de turno enviando el arreglo de montos declarados
 * estrictamente en la moneda nativa de cada método de pago.
 * @param {Array<{paymentMethodId: number, amount: number}>} declaredAmounts
 * @param {number|string|null|undefined} actualClosingBalanceUsd conteo USD opcional
 */
export async function closeShift(declaredAmounts, actualClosingBalanceUsd) {
  return await api.post('/api/shifts/close', buildCloseShiftPayload(declaredAmounts, actualClosingBalanceUsd));
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
