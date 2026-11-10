import { formatUSD, parseAmount } from '../../utils/formatters';
import { normalizeRole } from '../../navigation/roleViews';

/**
 * 8.187-T5 (Paso 11/12, F4, S1-S7): lógica pura del crédito CxC en el POS web.
 *
 * Todas las decisiones de cupo, mora, tasa y redondeo viven en el servidor; este módulo solo
 * traduce contratos a presentación y validaciones de forma (espejo del comportamiento del
 * cliente WPF de T4). Sin React ni DOM para que `node --test` lo ejercite directo.
 */

export const CREDIT_STATUS = Object.freeze({
  ACTIVE: 0,
  SUSPENDED_BY_MORA: 1,
  MANUALLY_BLOCKED: 2,
});

const VENEZUELA_TIME_ZONE = 'America/Caracas';

/** Estados manuales válidos del perfil (SuspendedByMora lo administra el sistema de mora). */
export const MANUAL_CREDIT_STATUSES = Object.freeze([CREDIT_STATUS.ACTIVE, CREDIT_STATUS.MANUALLY_BLOCKED]);

/**
 * 8.188 (Paso 11-13): roles con autorización de escritura del perfil crediticio. Espejo exacto
 * del RBAC del backend (PUT credit-profile = Admin/Manager); el cajero queda en solo lectura.
 */
export const CREDIT_PROFILE_MANAGER_ROLES = Object.freeze(['Admin', 'Manager']);

export function canEditCreditProfileByRole(role) {
  const canonical = normalizeRole(role);
  return canonical !== null && CREDIT_PROFILE_MANAGER_ROLES.includes(canonical);
}

/**
 * Habilita el formulario de edición del panel crediticio: rol habilitado, cliente real (nunca el
 * Consumidor Final) y perfil cargado sin suspensión de sistema. Con Cashier devuelve false y el
 * formulario se oculta por completo (no se renderiza deshabilitado).
 */
export function canManageCreditProfileForm({
  canEdit = false,
  isDefaultCustomer = false,
  profile = null,
  isSystemSuspended = false,
} = {}) {
  return Boolean(canEdit) && !isDefaultCustomer && profile != null && !isSystemSuspended;
}

export function creditStatusLabel(status) {
  switch (Number(status)) {
    case CREDIT_STATUS.ACTIVE:
      return 'Activo';
    case CREDIT_STATUS.SUSPENDED_BY_MORA:
      return 'Suspendido por Mora';
    case CREDIT_STATUS.MANUALLY_BLOCKED:
      return 'Bloqueado Manualmente';
    default:
      return 'Desconocido';
  }
}

/** El perfil impide operar a crédito (el cupo disponible NO bloquea acá: lo evalúa el checkout). */
export function isCreditProfileBlocked(profile) {
  return profile == null
    || profile.isActive !== true
    || profile.isCreditEnabled !== true
    || Number(profile.creditStatus) !== CREDIT_STATUS.ACTIVE
    || Number(profile.overdueCount) > 0;
}

/**
 * Mensaje único de bloqueo del modo crédito (mismas causas y textos del cliente WPF).
 * @returns {string|null} null cuando el cliente puede financiar.
 */
export function creditProfileBlockMessage({ hasRegisteredCustomer, isLoading = false, error = null, profile = null }) {
  if (!hasRegisteredCustomer) {
    return 'La venta a crédito exige un cliente registrado y con perfil crediticio activo. Seleccione un cliente para continuar.';
  }
  if (isLoading) {
    return 'Consultando el perfil crediticio del cliente...';
  }
  if (typeof error === 'string' && error.trim().length > 0) {
    return error;
  }
  if (profile == null) {
    return 'No se pudo consultar el perfil crediticio del cliente.';
  }
  if (profile.isActive !== true) {
    return 'El cliente está inactivo y no puede operar bajo modalidad de crédito.';
  }
  if (profile.isCreditEnabled !== true) {
    return 'El cliente no tiene la línea de crédito habilitada.';
  }
  if (Number(profile.creditStatus) === CREDIT_STATUS.SUSPENDED_BY_MORA) {
    return 'El crédito del cliente está suspendido por mora. Regularice la deuda para rehabilitar la línea.';
  }
  if (Number(profile.creditStatus) === CREDIT_STATUS.MANUALLY_BLOCKED) {
    return 'El crédito del cliente está bloqueado manualmente por un administrador.';
  }
  if (Number(profile.overdueCount) > 0) {
    return 'El cliente presenta obligaciones vencidas (mora) y no puede operar a crédito.';
  }
  return null;
}

/**
 * Validación de forma del perfil administrativo (espejo de las reglas del servidor):
 * cupo ≥ 0, plazo 1..365 entero, gracia ≥ 0, notas ≤ 1000 y estado manual válido.
 * @returns {string[]} mensajes de error; vacío = válido.
 */
export function validateCreditProfileUpdate(input = {}) {
  const errors = [];

  const limit = Number(input.creditLimitUSD);
  if (!Number.isFinite(limit) || limit < 0) {
    errors.push('El límite de crédito no puede ser negativo.');
  }

  const term = Number(input.paymentTermDays);
  if (!Number.isInteger(term) || term < 1 || term > 365) {
    errors.push('El plazo de pago debe estar entre 1 y 365 días continuos.');
  }

  const grace = Number(input.creditGraceDays);
  if (!Number.isInteger(grace) || grace < 0) {
    errors.push('Los días de gracia no pueden ser negativos.');
  }

  if (typeof input.creditNotes === 'string' && input.creditNotes.trim().length > 1000) {
    errors.push('Las notas crediticias no pueden exceder 1000 caracteres.');
  }

  const status = Number(input.creditStatus);
  if (status === CREDIT_STATUS.SUSPENDED_BY_MORA) {
    errors.push("El estado 'Suspendido por Mora' es administrado automáticamente por el sistema y no puede asignarse manualmente.");
  } else if (!MANUAL_CREDIT_STATUSES.includes(status)) {
    errors.push('El estado crediticio informado no es válido.');
  }

  return errors;
}

/**
 * Monto financiado del cierre a crédito: server-first (saldo remanente canónico del preview
 * fresco) con respaldo de presentación derivado del total exhibido. El cierre SIEMPRE valida
 * en el servidor.
 */
export function computeCreditCheckoutState({
  hasRegisteredCustomer,
  isLoadingProfile = false,
  profileError = null,
  profile = null,
  isPreviewFresh = false,
  previewRemainingUSD = null,
  previewRemainingBsS = null,
  fallbackFinancedUSD = 0,
  fallbackFinancedBsS = 0,
} = {}) {
  const previewUsd = Number(previewRemainingUSD);
  const previewBsS = Number(previewRemainingBsS);

  const financedUSD = isPreviewFresh && Number.isFinite(previewUsd)
    ? Math.max(0, previewUsd)
    : Math.max(0, Number(fallbackFinancedUSD) || 0);

  const financedBsS = isPreviewFresh && Number.isFinite(previewBsS)
    ? Math.max(0, previewBsS)
    : Math.max(0, Number(fallbackFinancedBsS) || 0);

  const profileBlockMessage = creditProfileBlockMessage({
    hasRegisteredCustomer,
    isLoading: isLoadingProfile,
    error: profileError,
    profile,
  });

  const isOverLimit = profile != null
    && !profileBlockMessage
    && financedUSD > Number(profile.availableCreditUSD || 0) + 0.005;

  const overLimitMessage = isOverLimit
    ? creditOverLimitMessage({ financedUSD, availableCreditUSD: Number(profile.availableCreditUSD || 0) })
    : null;

  const canFinalize = Boolean(hasRegisteredCustomer)
    && !isLoadingProfile
    && !(typeof profileError === 'string' && profileError.trim().length > 0)
    && profile != null
    && profileBlockMessage == null
    && !isOverLimit;

  return { financedUSD, financedBsS, profileBlockMessage, isOverLimit, overLimitMessage, canFinalize };
}

/**
 * Mensaje del bloqueo por sobregiro. El residual de esta fase: la terminal web NO abre flujo de
 * solicitud de autorización (el hub web no transporta el contexto CreditOverLimit); la
 * autorización se resuelve presencialmente desde una caja WPF y luego se reintenta acá.
 */
export function creditOverLimitMessage({ financedUSD = 0, availableCreditUSD = 0 } = {}) {
  return `El monto financiado (${formatUSD(Number(financedUSD) || 0)}) excede el crédito disponible (${formatUSD(Number(availableCreditUSD) || 0)}). `
    + 'Se requiere la autorización de un Administrador o Supervisor desde una caja WPF (módulo Cuentas por Cobrar); '
    + 'esta terminal web no envía solicitudes de autorización en esta fase.';
}

/**
 * Fecha de vencimiento estimada de una obligación nueva: hoy + plazo del cliente (días
 * continuos). Es referencial para el operador: el servidor ancla la fecha real al emitir.
 * @returns {Date|null}
 */
export function creditDueDate(paymentTermDays, baseDate = new Date()) {
  const days = Number(paymentTermDays);
  if (!Number.isFinite(days)) return null;
  const base = baseDate instanceof Date && !Number.isNaN(baseDate.getTime()) ? baseDate : new Date();
  return new Date(base.getFullYear(), base.getMonth(), base.getDate() + Math.max(0, Math.trunc(days)));
}

/**
 * Formatea una fecha de negocio (construida en hora local: vencimiento estimado) sin conversión
 * de zona horaria — a diferencia de `formatDateVe`, que traduce instantes UTC del servidor.
 * @returns {string} dd/mm/yyyy o '—'
 */
export function formatDueDateDisplay(date) {
  if (!(date instanceof Date) || Number.isNaN(date.getTime())) return '—';
  return `${String(date.getDate()).padStart(2, '0')}/${String(date.getMonth() + 1).padStart(2, '0')}/${date.getFullYear()}`;
}

/**
 * Formatea una fecha (ISO/Date) en el calendario de Venezuela para no correr el día cuando el
 * backend serializa UTC. Independiente del locale del runtime (en-CA → YYYY-MM-DD).
 * @returns {string} dd/mm/yyyy o '—'
 */
export function formatDateVe(value) {
  if (value === null || value === undefined || value === '') return '—';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return '—';

  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone: VENEZUELA_TIME_ZONE,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(date);
    const part = (type) => parts.find((p) => p.type === type)?.value;
    return `${part('day')}/${part('month')}/${part('year')}`;
  } catch {
    return '—';
  }
}

/**
 * Validación de forma del abono (mismas reglas/textos del modal WPF): monto > 0, método con
 * referencia cuando aplica y tope contra el saldo abierto (FIFO) o las facturas seleccionadas
 * (imputación explícita), con la tolerancia de polvo de 0.005 USD del servidor.
 * @returns {string|null} mensaje de bloqueo o null cuando es válido.
 */
export function validateCreditPaymentInput({
  amountUSD = 0,
  method = null,
  reference = '',
  isExplicitAllocation = false,
  selectedIds = [],
  selectedTotalUSD = 0,
  totalOpenUSD = 0,
} = {}) {
  const amount = Number(amountUSD) || 0;

  if (amount <= 0) {
    return 'Ingrese un monto de abono mayor a cero.';
  }
  if (!method) {
    return 'Seleccione el método de pago del abono.';
  }
  const ref = typeof reference === 'string' ? reference.trim() : '';
  if (method.requiresReference && ref.length === 0) {
    return 'El método de pago seleccionado exige número de referencia.';
  }

  if (isExplicitAllocation) {
    if (!Array.isArray(selectedIds) || selectedIds.length === 0) {
      return 'Seleccione al menos una factura para imputar el abono.';
    }
    if (amount > Number(selectedTotalUSD) + 0.005) {
      return 'El monto excede el saldo de las facturas seleccionadas.';
    }
  } else if (amount > Number(totalOpenUSD) + 0.005) {
    return 'El monto excede el saldo pendiente del cliente.';
  }

  return null;
}

/**
 * Proyección INFORMATIVA del monto en Bs.S a la tasa del día (solo display). El cobro real
 * congela la tasa server-side: acá no se implementa ninguna política de redondeo fiscal.
 */
export function receivableBsSProyeccion(amountUSD, todayRate) {
  const amount = Number(amountUSD) || 0;
  const rate = Number(todayRate) || 0;
  if (rate <= 0) return 0;
  return Math.round(amount * rate * 100) / 100;
}

/** Saldo resultante del cliente tras el abono capturado (USD, solo display). */
export function creditPaymentRemaining(totalOpenUSD, amountUSD) {
  return Math.max(0, (Number(totalOpenUSD) || 0) - (Number(amountUSD) || 0));
}

/** Alerta de mora de la cartera filtrada: el servidor reporta el vencido fuera de gracia
 * (mismo umbral de polvo decimal de 0.005 USD que usa la cartera). */
export function hasOverdueAlert(totalOverdueUSD) {
  return (Number(totalOverdueUSD) || 0) > 0.005;
}

const OPEN_RECEIVABLE_STATUSES = Object.freeze(['vigente', 'parcial', 'vencida']);

/** Obligación abierta: saldo vigente > 0.005 USD y estado abierto (polvo absorbido por el servidor). */
export function isReceivableOpen(item) {
  if (!item) return false;
  const status = String(item.status || '').toLowerCase();
  return Number(item.balanceUSD) > 0.005 && OPEN_RECEIVABLE_STATUSES.includes(status);
}

/** Anulación simple permitida solo sin abonos: saldo vigente igual al original. */
export function isReceivableCancelable(item) {
  return isReceivableOpen(item) && Number(item.balanceUSD) === Number(item.originalUSD);
}

/** Devolución permitida sobre obligaciones abiertas o pagadas (nunca anuladas). */
export function canReturnReceivable(item) {
  if (!item) return false;
  return isReceivableOpen(item) || String(item.status || '').toLowerCase() === 'pagada';
}

/** Opciones del filtro de estado de la cartera (''/Todos ⇒ sin filtro server-side). */
export const RECEIVABLE_STATUS_OPTIONS = Object.freeze(['Todos', 'Vigente', 'Parcial', 'Vencida', 'Pagada', 'Anulada']);

/**
 * Parsea el monto USD capturado por el operador (acepta coma decimal y separadores de miles)
 * y lo fija a 4dp (precisión de la deuda); el servidor revalida el saldo real.
 */
export function parseCreditAmount(rawValue) {
  const parsed = parseAmount(rawValue);
  if (!Number.isFinite(parsed)) return 0;
  return Math.round(parsed * 10000) / 10000;
}
