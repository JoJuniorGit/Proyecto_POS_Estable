import { api } from './api';
import { createIdempotencyKey } from './pettyCashApi';

/**
 * 8.212-WU5: client contract for the per-physical-method drawer balances and the method-aware
 * cash advance. The server remains the authority for balance and commission validation.
 */
export async function getPhysicalMethodBalances(sessionId, signal) {
  if (!sessionId) return [];
  const rows = await api.get(
    `/api/cashdrawer/physical-method-balances?sessionId=${sessionId}`,
    signal
  );
  return Array.isArray(rows) ? rows : [];
}

export function buildCashAdvancePayload({
  sessionId,
  requestedAmountLocal,
  paymentMethodId,
  paymentMethodName,
  isTransfer,
  exchangeRate,
  dispensedCashPaymentMethodId = null,
  dispensedAmount = null,
  receivedPaymentMethodId = null,
  cashierId = null,
  userName = null,
} = {}) {
  const payload = {
    sessionId,
    requestedAmountLocal: Number(requestedAmountLocal) || 0,
    paymentMethodId,
    paymentMethodName: paymentMethodName || '',
    isTransfer: Boolean(isTransfer),
    exchangeRate: Number(exchangeRate) || 1,
    cashierId,
    userName,
  };

  if (dispensedCashPaymentMethodId !== null && dispensedCashPaymentMethodId !== undefined) {
    payload.dispensedCashPaymentMethodId = dispensedCashPaymentMethodId;
    payload.dispensedAmount = Number(dispensedAmount) || 0;
    payload.receivedPaymentMethodId = receivedPaymentMethodId ?? paymentMethodId;
  }

  return payload;
}

export async function processCashAdvance(payload, options = {}) {
  const idempotencyKey = options.idempotencyKey || createIdempotencyKey();
  return await api.post('/api/cashdrawer/cash-advance', payload, {
    headers: { 'Idempotency-Key': idempotencyKey },
  });
}

export function formatPhysicalMethodAmount(method, amount) {
  const numeric = Number(amount) || 0;
  const symbol = method?.currencySymbol || method?.currencyCode || '';
  const formatted = numeric.toLocaleString('es-VE', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return symbol ? `${symbol} ${formatted}` : formatted;
}

export function resolveDispensedMethod(methods = [], selectedId) {
  const rows = Array.isArray(methods) ? methods : [];
  if (selectedId !== null && selectedId !== undefined && selectedId !== '') {
    const selected = rows.find((method) => String(method.paymentMethodId) === String(selectedId));
    if (selected) return selected;
  }

  if (rows.length === 1) return rows[0];
  return rows.find((method) => method.canDispense) || null;
}

export function validateDispensedBalance(method, requestedAmount) {
  if (!method) {
    return 'Seleccione el método físico que entrega el efectivo.';
  }

  if (method.isActive === false) {
    return `El método físico ${method.name} (${method.currencyCode}) no está activo para dispensar.`;
  }

  const requested = Number(requestedAmount) || 0;
  const balance = Number(method.currentBalance) || 0;
  if (requested > balance) {
    return `Existencia insuficiente en ${method.name} (${method.currencyCode}): disponible ${formatPhysicalMethodAmount(method, balance)}, requerido ${formatPhysicalMethodAmount(method, requested)}.`;
  }

  return null;
}
