import { api } from './api';

/**
 * T5 (Paso 13 / 8.188, S1/S4/S5/S7): cliente HTTP de caja chica y cierre de sesión del POS web.
 *
 * Espeja el contrato del backend y del cliente WPF (T4): el servidor resuelve la tasa BCV, la
 * valoración bimonetaria, la liquidez por gaveta y la autorización jerárquica. Acá NO se recalcula
 * ninguna política ni snapshot: solo se transportan los datos y se parsea el 403 del contrato.
 *
 * Residual T5 (documentado): el 403 `authorizationAction=PettyCashExpense` no se reintenta a
 * ciegas. Esta terminal web no transporta el contexto de caja chica en el hub de autorizaciones
 * en esta fase, por lo que la autorización presencial/supervisora se completa desde una caja WPF.
 */

export const PETTY_CASH_AUTHORIZATION_ACTION = 'PettyCashExpense';

/** Mensaje único del gate web de autorización (misma limitación que el 403 de crédito). */
export const PETTY_CASH_AUTHORIZATION_MESSAGE =
  'La operación de caja chica requiere autorización de un Administrador o Supervisor. ' +
  'En esta fase, la autorización debe realizarse de forma presencial desde una caja WPF (escritorio); ' +
  'esta terminal web no puede generar el token de autorización.';

export const PettyCashCurrency = Object.freeze({
  BsS: 0,
  Usd: 1,
});

export const PettyCashStatus = Object.freeze({
  Active: 0,
  Cancelled: 1,
});

export const PETTY_CASH_CURRENCY_OPTIONS = Object.freeze([
  Object.freeze({ value: PettyCashCurrency.BsS, label: 'Bs.S (bolívares)' }),
  Object.freeze({ value: PettyCashCurrency.Usd, label: 'USD (divisas)' }),
]);

// Mismas etiquetas formales del cliente WPF (S1: categorías tipificadas del mandato).
export const PETTY_CASH_CATEGORY_OPTIONS = Object.freeze([
  Object.freeze({ value: 0, label: 'Insumos y Limpieza' }),
  Object.freeze({ value: 1, label: 'Fletes y Acarreo' }),
  Object.freeze({ value: 2, label: 'Mantenimiento y Reparaciones' }),
  Object.freeze({ value: 3, label: 'Servicios Públicos/Eventuales' }),
  Object.freeze({ value: 4, label: 'Papelería' }),
  Object.freeze({ value: 5, label: 'Varios' }),
]);

const CATEGORY_LABELS = Object.freeze(
  PETTY_CASH_CATEGORY_OPTIONS.reduce((map, option) => ({ ...map, [option.value]: option.label }), {})
);

export function getPettyCashCategoryLabel(category) {
  return CATEGORY_LABELS[Number(category)] ?? 'Varios';
}

export function getPettyCashCurrencyLabel(currency) {
  return Number(currency) === PettyCashCurrency.Usd ? 'USD' : 'Bs.S';
}

export function getPettyCashStatusLabel(status) {
  return Number(status) === PettyCashStatus.Cancelled ? 'Anulado' : 'Activo';
}

function normalizeInt(value, fallback = 0) {
  const parsed = Number(value);
  return Number.isInteger(parsed) ? parsed : fallback;
}

function trimOrEmpty(value) {
  return typeof value === 'string' ? value.trim() : '';
}

function createIdempotencyKey() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `petty-cash-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
}

/**
 * Los headers del registro/anulación: la clave de idempotencia viaja también en su header para
 * que el retry de transporte de `api.js` (503/504) reuse el mismo payload y el servidor lo
 * resuelva como replay; el token supervisor es opcional (solo en el reintento del contrato).
 */
function buildAuthorizationOptions({ authorizationToken = null, idempotencyKey = null, signal } = {}) {
  const headers = {};
  if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
  if (authorizationToken) headers['X-Authorization-Token'] = authorizationToken;
  return { headers, signal };
}

/**
 * Registra el egreso de caja chica del turno. El cuerpo envía SOLO el nominal de la divisa física,
 * la categoría y el detalle documental: la tasa, el contravalor dual y la liquidez son del servidor.
 */
export async function registerPettyCashExpense(payload = {}, options = {}) {
  const idempotencyKey = options.idempotencyKey || payload.idempotencyKey || createIdempotencyKey();

  return await api.post('/api/cashdrawer/petty-cash', {
    currency: normalizeInt(payload.currency, PettyCashCurrency.BsS),
    amount: Number(payload.amount) || 0,
    category: normalizeInt(payload.category, 0),
    concept: trimOrEmpty(payload.concept),
    beneficiary: trimOrEmpty(payload.beneficiary),
    referenceNumber: trimOrEmpty(payload.referenceNumber),
    idempotencyKey,
  }, buildAuthorizationOptions({ ...options, idempotencyKey }));
}

/** Anula un gasto activo de la sesión abierta con motivo obligatorio (reversa compensatoria). */
export async function cancelPettyCashExpense(expenseId, reason, options = {}) {
  return await api.post(
    `/api/cashdrawer/petty-cash/${expenseId}/cancel`,
    { reason: trimOrEmpty(reason) },
    buildAuthorizationOptions(options)
  );
}

/** Gastos del turno (activos y anulados) para el listado de caja y el vale. */
export async function getPettyCashExpenses(sessionId, signal) {
  return await api.get(`/api/cashdrawer/petty-cash?sessionId=${sessionId}`, signal);
}

/** Saldos físicos teóricos por gaveta (Admin/Manager: arqueo ciego de supervisión). */
export async function getPettyCashPhysicalBalances(sessionId, signal) {
  return await api.get(`/api/cashdrawer/physical-balances?sessionId=${sessionId}`, signal);
}

/** Vale de caja chica PDF on-demand (`VCC-000001.pdf`); el servidor decide el acceso. */
export async function getPettyCashVoucher(expenseId, signal) {
  return await api.getBlob(`/api/cashdrawer/petty-cash/${expenseId}/receipt`, signal);
}

/**
 * Cierre de la sesión de caja con conteo USD opcional (T2b, S2). El conteo Bs.S es obligatorio y
 * `actualClosingBalanceUsd` viaja null cuando no se informa: el comportamiento previo se conserva.
 */
export async function closeCashDrawerSession(payload = {}, signal) {
  const rawUsd = payload?.actualClosingBalanceUsd;
  const hasUsd = rawUsd !== null && rawUsd !== undefined && rawUsd !== ''
    && Number.isFinite(Number(rawUsd));

  return await api.post('/api/cashdrawer/close', {
    actualClosingBalanceLocal: Number(payload?.actualClosingBalanceLocal) || 0,
    currentExchangeRate: Number(payload?.currentExchangeRate) || 0,
    actualClosingBalanceUsd: hasUsd ? Number(rawUsd) : null,
  }, { signal });
}

/** true cuando el error es el 403 del gate de caja chica (authorizationAction explícito). */
export function isPettyCashAuthorizationError(error) {
  return error?.authorizationAction === PETTY_CASH_AUTHORIZATION_ACTION
    || error?.body?.authorizationAction === PETTY_CASH_AUTHORIZATION_ACTION;
}

/**
 * Contexto exacto del 403 (authorizationContext del ProblemDetails): sesión, divisa, montos
 * bimonetarios y categoría. Null si el error no es del contrato de caja chica.
 * @returns {{sessionId: number|null, currency: number|null, amountBss: number, amountUsd: number, category: number|null}|null}
 */
export function extractPettyCashAuthorizationContext(error) {
  if (!isPettyCashAuthorizationError(error)) return null;

  const raw = error?.body && typeof error.body === 'object' ? error.body.authorizationContext : null;
  if (!raw || typeof raw !== 'object') return null;

  const amountBss = Number(raw.amountBss ?? raw.amountBsS);
  const amountUsd = Number(raw.amountUsd);

  return {
    sessionId: Number.isInteger(Number(raw.sessionId)) ? Number(raw.sessionId) : null,
    currency: Number.isInteger(Number(raw.currency)) ? Number(raw.currency) : null,
    amountBss: Number.isFinite(amountBss) ? amountBss : 0,
    amountUsd: Number.isFinite(amountUsd) ? amountUsd : 0,
    category: Number.isInteger(Number(raw.category)) ? Number(raw.category) : null,
  };
}

/**
 * Mensaje claro del gate web: autorización presencial/supervisora desde una caja WPF en esta fase.
 * Adjunta el contexto del 403 (sesión, categoría y montos duales) para que el operador sepa qué
 * operación quedó pendiente de autorización. Los formateadores son opcionales (fallback 2dp).
 */
export function buildPettyCashAuthorizationMessage(error, formatters = {}) {
  const formatBsS = typeof formatters.formatBsS === 'function'
    ? formatters.formatBsS
    : (value) => `Bs.S ${(Number(value) || 0).toFixed(2)}`;
  const formatUSD = typeof formatters.formatUSD === 'function'
    ? formatters.formatUSD
    : (value) => `$ ${(Number(value) || 0).toFixed(2)}`;

  const context = extractPettyCashAuthorizationContext(error);
  if (!context) return PETTY_CASH_AUTHORIZATION_MESSAGE;

  const parts = [];
  if (context.sessionId !== null) parts.push(`Sesión #${context.sessionId}`);
  if (context.category !== null) parts.push(getPettyCashCategoryLabel(context.category));
  parts.push(`${formatBsS(context.amountBss)} / ${formatUSD(context.amountUsd)}`);

  return `${PETTY_CASH_AUTHORIZATION_MESSAGE} Operación pendiente: ${parts.join(' · ')}.`;
}
