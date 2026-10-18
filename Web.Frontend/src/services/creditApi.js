import { api } from './api';

/**
 * 8.187-T5 (Paso 11/12, F4, S1-S7): cliente HTTP del crédito CxC en el POS web.
 *
 * Espeja el contrato del backend y del cliente WPF (T4): la tasa del abono la resuelve el
 * servidor desde la fuente BCV cacheada y la UI solo consume montos/estados. Acá NO se
 * implementa política de cupo, mora ni redondeo fiscal: el servidor es la única autoridad.
 *
 * Residual T5: el 403 `authorizationAction=CreditOverLimit` se traduce a un mensaje claro que
 * pide la autorización presencial desde una caja WPF; esta terminal web no tiene flujo de
 * solicitud de autorización en esta fase (el hub web no transporta el contexto de sobregiro).
 */

export const CREDIT_OVER_LIMIT_ACTION = 'CreditOverLimit';

/**
 * Perfil crediticio del cliente: cupo, deuda, disponible, utilización y mora en tiempo real.
 * Cliente inexistente → 404 (ApiError).
 */
export async function getCreditProfile(customerId, signal) {
  return await api.get(`/api/sales/customers/${customerId}/credit-profile`, signal);
}

/**
 * Escritura administrativa del perfil (solo Admin/Manager en el backend). `creditStatus` viaja
 * como int del enum (Active=0 / ManuallyBlocked=2); SuspendedByMora es administrado por el
 * sistema y el servidor lo rechaza con 400.
 */
export async function updateCreditProfile(customerId, profile) {
  const notes = typeof profile?.creditNotes === 'string' ? profile.creditNotes.trim() : '';
  return await api.put(`/api/sales/customers/${customerId}/credit-profile`, {
    isCreditEnabled: Boolean(profile?.isCreditEnabled),
    creditLimitUSD: Number(profile?.creditLimitUSD) || 0,
    paymentTermDays: Number(profile?.paymentTermDays) || 0,
    creditGraceDays: Number(profile?.creditGraceDays) || 0,
    creditNotes: notes.length > 0 ? notes : null,
    creditStatus: Number(profile?.creditStatus) || 0,
  });
}

/**
 * Estado de cuenta dual del cliente: perfil + cartera completa con equivalente Bs.S del día +
 * historial de abonos con el diferencial cambiario realizado. Cliente inexistente → 404.
 */
export async function getCreditStatement(customerId, signal) {
  return await api.get(`/api/sales/customers/${customerId}/credit-statement`, signal);
}

/**
 * Construye la query de la cartera global con los mismos filtros del cliente WPF: `customerId`,
 * `status` (nombre del enum), `overdueOnly` (mora dinámica) y `search` (cliente/cédula/factura).
 * Los filtros vacíos se omiten para no ensuciar la URL.
 * @param {{customerId?: number|null, status?: string|null, overdueOnly?: boolean, search?: string|null}} filter
 * @returns {string}
 */
export function buildCreditReceivablesQuery(filter = {}) {
  const parts = [];

  if (filter.customerId !== null && filter.customerId !== undefined && filter.customerId !== '') {
    parts.push(`customerId=${encodeURIComponent(filter.customerId)}`);
  }

  const status = typeof filter.status === 'string' ? filter.status.trim() : '';
  if (status) {
    parts.push(`status=${encodeURIComponent(status)}`);
  }

  if (filter.overdueOnly === true) {
    parts.push('overdueOnly=true');
  }

  const search = typeof filter.search === 'string' ? filter.search.trim() : '';
  if (search) {
    parts.push(`search=${encodeURIComponent(search)}`);
  }

  return `/api/sales/credit-receivables${parts.length > 0 ? `?${parts.join('&')}` : ''}`;
}

/**
 * Cartera global de cuentas por cobrar con filtros y totales abiertos/vencidos del conjunto
 * filtrado, más la tasa efectiva del día usada para la proyección Bs.S.
 */
export async function getCreditReceivables(filter = {}, signal) {
  return await api.get(buildCreditReceivablesQuery(filter), signal);
}

/**
 * Cierra una venta a crédito (total o mixta): los pagos iniciales que se informen atraviesan el
 * mismo camino del contado; el remanente no cubierto se financia como Cuenta por Cobrar. El
 * `authorizationToken` (flujo de espera) es opcional y viaja en X-Authorization-Token; el
 * backend no expone Idempotency-Key para este endpoint (residual T2/T3).
 * @returns {Promise<number>} Número de factura
 */
export async function completeCreditSale(saleId, { exchangeRate, payments = [], cashierId = null, authorizationToken = null } = {}) {
  const options = {};
  if (authorizationToken) {
    options.headers = { 'X-Authorization-Token': authorizationToken };
  }
  return await api.post(`/api/sales/${saleId}/complete-credit`, {
    exchangeRate,
    cashierId,
    payments,
  }, options);
}

function createIdempotencyKey(prefix) {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `${prefix}-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

/**
 * Registra un abono CxC. El monto se expresa en USD (la deuda está denominada en USD) y la tasa
 * la resuelve el servidor; `receivableIds` vacío/nulo ⇒ FIFO de cartera, con valores ⇒ imputación
 * explícita en el orden informado. La clave de idempotencia viaja en el cuerpo y en el header
 * (mismo contrato del cliente WPF): un reintento replica el abono sin nuevos movimientos.
 */
export async function registerCreditPayment(payment, idempotencyKey = null) {
  const key = idempotencyKey || createIdempotencyKey('credit-pay');
  const receivableIds = Array.isArray(payment?.receivableIds) && payment.receivableIds.length > 0
    ? payment.receivableIds
    : null;

  return await api.post('/api/sales/credit-payments', {
    customerId: payment?.customerId,
    amountUSD: payment?.amountUSD,
    paymentMethodId: payment?.paymentMethodId,
    referenceNumber: payment?.referenceNumber ?? null,
    receivableIds,
    idempotencyKey: key,
  }, {
    headers: { 'Idempotency-Key': key },
  });
}

/**
 * Anula una venta a crédito SIN abonos: restituye stock y cupo, sin movimientos de caja. Con
 * abonos previos el servidor responde 409 y el camino correcto es la devolución.
 */
export async function cancelCreditSale(saleId, reason = null) {
  const trimmed = typeof reason === 'string' ? reason.trim() : '';
  return await api.post(`/api/sales/${saleId}/credit-cancel`, {
    reason: trimmed.length > 0 ? trimmed : null,
  });
}

/**
 * Registra la devolución total o parcial de una venta a crédito. El valor por línea lo calcula
 * el servidor sobre la base efectiva; la clave de idempotencia viaja en cuerpo y header.
 * @param {number} saleId
 * @param {{items: Array<{saleItemId: number, quantity: number}>, reason?: string|null}} payload
 * @param {string|null} [idempotencyKey]
 */
export async function registerCreditReturn(saleId, payload = {}, idempotencyKey = null) {
  const key = idempotencyKey || createIdempotencyKey('credit-ret');
  const trimmed = typeof payload.reason === 'string' ? payload.reason.trim() : '';
  return await api.post(`/api/sales/${saleId}/credit-return`, {
    items: (payload.items || []).map((item) => ({
      saleItemId: item.saleItemId,
      quantity: item.quantity,
    })),
    reason: trimmed.length > 0 ? trimmed : null,
    idempotencyKey: key,
  }, {
    headers: { 'Idempotency-Key': key },
  });
}

/**
 * Comprobante de venta a crédito (documento de entrega) como PDF. Solo existe para ventas con
 * obligación CxC (400 si la venta no es a crédito; 404 si no existe).
 */
export async function getCreditSaleReceipt(saleId, signal) {
  return await api.getBlob(`/api/sales/${saleId}/credit-receipt`, signal);
}

/** Comprobante de abono / recibo de pago como PDF (saldo anterior/nuevo, tasa y FX del cobro). */
export async function getCreditPaymentReceipt(paymentId, signal) {
  return await api.getBlob(`/api/sales/credit-payments/${paymentId}/receipt`, signal);
}

/** true cuando el 403 del backend es el gate de sobregiro de cupo (CreditOverLimit). */
export function isCreditOverLimitError(error) {
  return error?.authorizationAction === CREDIT_OVER_LIMIT_ACTION;
}

/**
 * Contexto exacto del 403 de sobregiro (authorizationContext del ProblemDetails): cliente,
 * venta, monto financiado y crédito disponible. Null si el error no es CreditOverLimit.
 * @returns {{saleId: number|null, customerId: number|null, customerName: string|null, financedUSD: number, availableCreditUSD: number}|null}
 */
export function extractCreditOverLimitContext(error) {
  if (!isCreditOverLimitError(error)) return null;

  const raw = error?.body && typeof error.body === 'object' ? error.body.authorizationContext : null;
  if (!raw || typeof raw !== 'object') return null;

  const financedUSD = Number(raw.financedUSD);
  const availableCreditUSD = Number(raw.availableCreditUSD);

  return {
    saleId: Number.isFinite(Number(raw.saleId)) ? Number(raw.saleId) : null,
    customerId: Number.isFinite(Number(raw.customerId)) ? Number(raw.customerId) : null,
    customerName: typeof raw.customerName === 'string' ? raw.customerName : null,
    financedUSD: Number.isFinite(financedUSD) ? financedUSD : 0,
    availableCreditUSD: Number.isFinite(availableCreditUSD) ? availableCreditUSD : 0,
  };
}
