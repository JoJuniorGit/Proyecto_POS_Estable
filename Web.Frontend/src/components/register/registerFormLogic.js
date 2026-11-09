import {
  PETTY_CASH_CATEGORY_OPTIONS,
  PettyCashCurrency,
  PettyCashStatus,
} from '../../services/pettyCashApi';
import {
  MONETARY_MODE_DUAL,
  displayCurrencyCode,
  normalizeCurrencyCode,
  symbolForCode,
} from '../../utils/monetaryProfile';

/**
 * T5 (Paso 13 / 8.188): lógica pura de presentación de los formularios de caja en el POS web.
 * Solo proyecciones informativas y validaciones espejo del contrato: el servidor sigue siendo la
 * única autoridad de tasa, snapshots bimonetarios, liquidez y autorización jerárquica. El gating
 * de roles de acá es de presentación (qué acción se ofrece), nunca una política de negocio.
 */

const PETTY_CASH_ROLES = Object.freeze(['Admin', 'Manager', 'Cashier']);
const CLOSE_SESSION_ROLES = Object.freeze(['Admin', 'Manager']);

export function canUsePettyCash(role) {
  return PETTY_CASH_ROLES.includes(role);
}

export function canCloseCashDrawerSession(role) {
  return CLOSE_SESSION_ROLES.includes(role);
}

/**
 * 8.211-T4b2b2a (S9): moneda física local de la gaveta. En `Dual` es la secundaria; en `Single`
 * la primaria. `label` es el código de presentación (VES se mantiene como `Bs.S`) y `symbol` el
 * símbolo configurado por el perfil. Solo presentación: no interpreta ni recalcula montos.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 * @returns {{ isDual: boolean, code: string | null, label: string, symbol: string }}
 */
export function resolveLocalCurrencyProfile(profile = {}) {
  const isDual = profile?.mode === MONETARY_MODE_DUAL;
  const code = normalizeCurrencyCode(
    isDual ? profile?.secondaryCurrencyCode : profile?.primaryCurrencyCode
  );
  const label = displayCurrencyCode(code);
  const symbol = code ? symbolForCode(profile, code) : '';
  return { isDual, code, label, symbol: symbol || label };
}

/**
 * Indica si una moneda pertenece al perfil activo (primaria o secundaria en `Dual`).
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 * @param {unknown} code
 * @returns {boolean}
 */
export function profileIncludesCurrency(profile = {}, code) {
  const canonical = normalizeCurrencyCode(code);
  if (!canonical) return false;
  if (normalizeCurrencyCode(profile?.primaryCurrencyCode) === canonical) return true;
  return profile?.mode === MONETARY_MODE_DUAL
    && normalizeCurrencyCode(profile?.secondaryCurrencyCode) === canonical;
}

/**
 * Orden de monedas del perfil para la gaveta: local primero, luego primaria y secundaria.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 * @returns {{ localCode: string | null, order: string[] }}
 */
function profileCurrencyOrder(profile = {}) {
  const local = resolveLocalCurrencyProfile(profile);
  const primary = normalizeCurrencyCode(profile?.primaryCurrencyCode);
  const secondary = profile?.mode === MONETARY_MODE_DUAL
    ? normalizeCurrencyCode(profile?.secondaryCurrencyCode)
    : null;

  const order = [];
  const seen = new Set();
  for (const code of [local.code, primary, secondary]) {
    if (!code || seen.has(code)) continue;
    seen.add(code);
    order.push(code);
  }

  return { localCode: local.code, order };
}

function methodCurrencyCode(method) {
  return normalizeCurrencyCode(method?.currencyCode);
}

/**
 * Construye el resumen de existencia física por moneda. Incluye todas las monedas del perfil
 * (local primero) y cualquier moneda extra presente en los métodos; las ausentes quedan en 0.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 * @param {Array<{ currencyCode?: string, currentBalance?: number }>} methods
 * @returns {Array<{ code: string, label: string, symbol: string, total: number, isLocal: boolean }>}
 */
export function buildPhysicalCurrencySummaries(profile = {}, methods = []) {
  const rows = Array.isArray(methods) ? methods : [];
  const { localCode, order } = profileCurrencyOrder(profile);
  const codes = [...order];
  const seen = new Set(codes);

  for (const method of rows) {
    const code = methodCurrencyCode(method);
    if (code && !seen.has(code)) {
      seen.add(code);
      codes.push(code);
    }
  }

  return codes.map((code) => {
    const total = rows.reduce(
      (sum, method) => (methodCurrencyCode(method) === code ? sum + (Number(method?.currentBalance) || 0) : sum),
      0
    );

    return {
      code,
      label: displayCurrencyCode(code),
      symbol: symbolForCode(profile, code) || displayCurrencyCode(code),
      total,
      isLocal: code === localCode,
    };
  });
}

/**
 * Ordena los métodos físicos por prioridad de moneda (local primero) y luego por nombre/ID.
 * @param {Array<{ currencyCode?: string, name?: string, paymentMethodId?: number }>} methods
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 * @returns {Array}
 */
export function sortPhysicalMethodBalances(methods = [], profile = {}) {
  const rows = Array.isArray(methods) ? methods : [];
  const { order } = profileCurrencyOrder(profile);
  const priorityByCode = new Map(order.map((code, index) => [code, index]));
  const priorityFor = (method) => {
    const code = methodCurrencyCode(method);
    if (!code) return order.length;
    return priorityByCode.has(code) ? priorityByCode.get(code) : order.length;
  };

  return [...rows].sort((left, right) => {
    const priorityDiff = priorityFor(left) - priorityFor(right);
    if (priorityDiff !== 0) return priorityDiff;

    const nameDiff = String(left?.name || '').localeCompare(String(right?.name || ''), 'es', { sensitivity: 'base' });
    if (nameDiff !== 0) return nameDiff;

    return (Number(left?.paymentMethodId) || 0) - (Number(right?.paymentMethodId) || 0);
  });
}

/**
 * La anulación exige token supervisor para Manager y Cashier (S5); solo Admin anula directo.
 * Es gating de presentación (qué aviso se muestra), nunca una política: el servidor decide; el
 * modal abre la espera de autorización y reintenta con el token (residual 3).
 */
export function requiresPettyCashCancellationAuthorization(role) {
  return role !== 'Admin';
}

/** Redondeo fiscal AwayFromZero (Math.round ya es half-up para valores no negativos). */
export function roundHalfAwayFromZero(value, decimals) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) return 0;
  const factor = 10 ** decimals;
  return Math.round(numeric * factor) / factor;
}

function isBlank(value) {
  return value === null || value === undefined || (typeof value === 'string' && value.trim().length === 0);
}

/**
 * Proyección dual en vivo a la tasa vigente (display). Mismas fórmulas del servidor
 * (S1: Bs.S 2dp / USD 4dp, AwayFromZero) pero el snapshot definitivo lo fija el backend.
 * @returns {{bsS: number, usd: number, hasRate: boolean}}
 */
export function computePettyCashEquivalent({ currency, amount, exchangeRate } = {}) {
  const nominal = Number(amount) || 0;
  const rate = Number(exchangeRate) || 0;

  if (rate <= 0) {
    return { bsS: 0, usd: 0, hasRate: false };
  }

  if (Number(currency) === PettyCashCurrency.Usd) {
    return {
      bsS: roundHalfAwayFromZero(nominal * rate, 2),
      usd: roundHalfAwayFromZero(nominal, 4),
      hasRate: true,
    };
  }

  return {
    bsS: roundHalfAwayFromZero(nominal, 2),
    usd: roundHalfAwayFromZero(nominal / rate, 4),
    hasRate: true,
  };
}

/** Validación espejo del registro (S1): devuelve el primer error o null. */
export function validatePettyCashDraft(draft = {}) {
  const { exchangeRate, amount, category, concept, beneficiary, referenceNumber } = draft;

  if (!(Number(exchangeRate) > 0)) return 'No hay una tasa de cambio vigente para valorar el gasto.';
  if (!(Number(amount) > 0)) return 'Ingrese un monto mayor a cero.';

  const categoryNumber = isBlank(category) ? Number.NaN : Number(category);
  const categoryValid = Number.isInteger(categoryNumber)
    && PETTY_CASH_CATEGORY_OPTIONS.some((option) => option.value === categoryNumber);
  if (!categoryValid) return 'Seleccione la categoría operativa del gasto.';

  const conceptText = typeof concept === 'string' ? concept.trim() : '';
  if (!conceptText) return 'Ingrese el concepto del gasto.';
  if (conceptText.length > 500) return 'El concepto no puede exceder 500 caracteres.';

  const beneficiaryText = typeof beneficiary === 'string' ? beneficiary.trim() : '';
  if (!beneficiaryText) return 'Ingrese el beneficiario o proveedor.';
  if (beneficiaryText.length > 150) return 'El beneficiario no puede exceder 150 caracteres.';

  const referenceText = typeof referenceNumber === 'string' ? referenceNumber.trim() : '';
  if (!referenceText) return 'Ingrese la referencia del comprobante.';
  if (referenceText.length > 100) return 'La referencia no puede exceder 100 caracteres.';

  return null;
}

/** Validación espejo de la anulación (S5): motivo obligatorio, máximo 500. */
export function validatePettyCashCancelReason(reason) {
  const text = typeof reason === 'string' ? reason.trim() : '';
  if (!text) return 'Ingrese el motivo de la anulación.';
  if (text.length > 500) return 'El motivo no puede exceder 500 caracteres.';
  return null;
}

/**
 * Validación de la apertura (8.201, S1): ambos fondos son >= 0 y la tasa debe estar configurada.
 * Un fondo en cero es válido (caja sin fondo inicial).
 */
export function validateCashDrawerOpenDraft({
  openingBalanceLocal,
  openingBalanceUsd,
  currentExchangeRate,
  localCurrencyLabel = 'Bs.S',
  usdCurrencyLabel = 'USD',
} = {}) {
  const local = Number(openingBalanceLocal ?? 0);
  if (!Number.isFinite(local) || local < 0) {
    return `Ingrese un fondo inicial válido en ${localCurrencyLabel} (mayor o igual a cero).`;
  }

  const usd = Number(openingBalanceUsd ?? 0);
  if (!Number.isFinite(usd) || usd < 0) {
    return `Ingrese un fondo inicial válido en ${usdCurrencyLabel} (mayor o igual a cero).`;
  }

  const rate = Number(currentExchangeRate);
  if (!Number.isFinite(rate) || rate <= 0) {
    return 'Configure la tasa de cambio vigente antes de abrir la caja.';
  }

  return null;
}

/** Payload de apertura dual: ambos fondos viajan siempre (0 es un valor válido). */
export function buildCashDrawerOpenPayload({
  openingBalanceLocal,
  openingBalanceUsd,
  currentExchangeRate,
} = {}) {
  return {
    openingBalanceLocal: Number(openingBalanceLocal) || 0,
    openingBalanceUsd: Number(openingBalanceUsd) || 0,
    currentExchangeRate: Number(currentExchangeRate) || 0,
  };
}

/**
 * 8.212-WU5: derives the legacy local/USD opening aggregates from the explicit per-method rows and
 * returns the `methodBalances` payload. The server remains the authority for profile filtering.
 */
export function buildCashDrawerOpenMethodBreakdown({
  methods = [],
  amounts = {},
  localCurrencyCode = 'VES',
} = {}) {
  let openingBalanceLocal = 0;
  let openingBalanceUsd = 0;
  const methodBalances = [];

  for (const method of methods) {
    const amount = Number(amounts[method.id] ?? 0);
    if (!Number.isFinite(amount) || amount < 0) {
      return {
        error: `Ingrese un fondo inicial válido para ${method.name} (${method.currency || method.id}) (mayor o igual a cero).`,
        openingBalanceLocal: 0,
        openingBalanceUsd: 0,
        methodBalances: [],
      };
    }

    methodBalances.push({ paymentMethodId: method.id, openingAmount: amount });
    const currencyCode = normalizeCurrencyCode(method.currency) || localCurrencyCode;
    if (currencyCode === localCurrencyCode) {
      openingBalanceLocal += amount;
    } else if (currencyCode === 'USD') {
      openingBalanceUsd += amount;
    }
  }

  return { error: null, openingBalanceLocal, openingBalanceUsd, methodBalances };
}

/**
 * Validación del cierre de sesión (S2): el conteo Bs.S es obligatorio (>= 0) y el USD, cuando se
 * informa, también debe ser >= 0. Vacío es válido (el contrato conserva el comportamiento previo).
 */
export function validateCashDrawerCloseDraft({
  actualClosingBalanceLocal,
  actualClosingBalanceUsd,
  localCurrencyLabel = 'Bs.S',
  usdCurrencyLabel = 'USD',
} = {}) {
  if (isBlank(actualClosingBalanceLocal)) {
    return `Ingrese un conteo válido de efectivo ${localCurrencyLabel} (mayor o igual a cero).`;
  }

  const local = Number(actualClosingBalanceLocal);
  if (!Number.isFinite(local) || local < 0) {
    return `Ingrese un conteo válido de efectivo ${localCurrencyLabel} (mayor o igual a cero).`;
  }

  if (!isBlank(actualClosingBalanceUsd)) {
    const usd = Number(actualClosingBalanceUsd);
    if (!Number.isFinite(usd) || usd < 0) {
      return `Ingrese un conteo válido de efectivo ${usdCurrencyLabel} (mayor o igual a cero) o déjelo vacío.`;
    }
  }

  return null;
}

/** Payload del cierre: el USD omitido viaja como null (contrato T2b/S2). */
export function buildCashDrawerClosePayload({
  actualClosingBalanceLocal,
  actualClosingBalanceUsd,
  currentExchangeRate,
} = {}) {
  const rawUsd = actualClosingBalanceUsd;
  const hasUsd = !isBlank(rawUsd) && Number.isFinite(Number(rawUsd));

  return {
    actualClosingBalanceLocal: Number(actualClosingBalanceLocal) || 0,
    currentExchangeRate: Number(currentExchangeRate) || 0,
    actualClosingBalanceUsd: hasUsd ? Number(rawUsd) : null,
  };
}

/** Correlativo de presentación del vale (VCC-000001), con fallback desde el número crudo. */
export function getPettyCashVoucherCode(expense = {}) {
  if (typeof expense?.voucherCode === 'string' && expense.voucherCode.length > 0) {
    return expense.voucherCode;
  }
  const number = Number(expense?.voucherNumber);
  if (Number.isInteger(number) && number > 0) {
    return `VCC-${String(number).padStart(6, '0')}`;
  }
  return '—';
}

export function getPettyCashVoucherFileName(expense = {}) {
  const code = getPettyCashVoucherCode(expense);
  return code === '—' ? 'VCC.pdf' : `${code}.pdf`;
}

/** Autorizador del gasto: el backend expone el id (mismo display del listado WPF). */
export function getPettyCashAuthorizerDisplay(expense = {}) {
  const authorizerId = expense?.authorizedByUserId;
  if (authorizerId === null || authorizerId === undefined) return '—';
  return Number.isInteger(Number(authorizerId)) ? `Usuario #${Number(authorizerId)}` : '—';
}

export function getPettyCashCreatedAtDisplay(createdAtUtc) {
  if (!createdAtUtc) return '—';
  const date = new Date(createdAtUtc);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('es-VE', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

/** Resumen del panel: solo los gastos Active cuentan (los anulados netean en 0). */
export function summarizePettyCashExpenses(expenses = []) {
  const active = (Array.isArray(expenses) ? expenses : [])
    .filter((expense) => Number(expense?.status) === PettyCashStatus.Active);

  return {
    activeCount: active.length,
    totalBsS: roundHalfAwayFromZero(active.reduce((sum, expense) => sum + (Number(expense?.amountBsS) || 0), 0), 2),
    totalUsd: roundHalfAwayFromZero(active.reduce((sum, expense) => sum + (Number(expense?.amountUsd) || 0), 0), 2),
  };
}
