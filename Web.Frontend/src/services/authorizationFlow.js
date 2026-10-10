// 8.150 (T7, design D7): maquina de estados pura del flujo de espera web.
// Sin dependencias de React ni del DOM para que `node --test` la ejercite directo.

export const AUTHORIZATION_PHASES = Object.freeze({
  IDLE: 'idle',
  WAITING: 'waiting',
  LOCAL: 'local',
  GRANTED: 'granted',
  REJECTED: 'rejected',
  EXPIRED: 'expired',
});

// Textos exactos de la spec (remote-authorization-clients) + etiquetas minimas del
// formulario local alineadas con LoginPage ("Usuario"/"Contraseña").
export const AUTHORIZATION_STRINGS = Object.freeze({
  waiting: 'Esperando autorización remota...',
  localAuthorization: 'Autorización Local',
  rejected: 'Solicitud rechazada.',
  expired: 'Expirada',
  invalidCredentials: 'Credenciales inválidas o sin privilegios para autorizar.',
  retry: 'Reintentar',
  cancel: 'Cancelar',
  username: 'Usuario',
  password: 'Contraseña',
  authorize: 'Autorizar',
});

export function createAuthorizationFlowState() {
  return {
    phase: AUTHORIZATION_PHASES.IDLE,
    requestId: null,
    context: null,
    expiresAt: null,
    deduplicated: false,
    token: null,
    resolutionMode: null,
    resolvedByName: null,
    reason: null,
  };
}

export function startAuthorizationWait(state, { requestId, expiresAt, context = null, deduplicated = false } = {}) {
  const base = state ?? createAuthorizationFlowState();
  return {
    ...base,
    phase: AUTHORIZATION_PHASES.WAITING,
    requestId,
    context,
    expiresAt,
    deduplicated: Boolean(deduplicated),
    token: null,
    resolutionMode: null,
    resolvedByName: null,
    reason: null,
  };
}

export function applyAuthorizationResolution(
  state,
  { approved, token = null, reason = null, resolutionMode = 'Remote', resolvedByName = null } = {}
) {
  if (!state || state.phase !== AUTHORIZATION_PHASES.WAITING) return state;

  if (approved) {
    return {
      ...state,
      phase: resolutionMode === 'Local' ? AUTHORIZATION_PHASES.LOCAL : AUTHORIZATION_PHASES.GRANTED,
      token,
      resolutionMode,
      resolvedByName,
      reason: null,
    };
  }

  return {
    ...state,
    phase: AUTHORIZATION_PHASES.REJECTED,
    token: null,
    resolutionMode: null,
    resolvedByName,
    reason: reason ?? null,
  };
}

// Aprobacion sin token recuperable (ventana vencida/borde de reconexion) se degrada a
// "Expirada": sin token no se puede reanudar la accion y el retry inicia una solicitud nueva.
export function applyAuthorizationExpiry(state) {
  if (!state || state.phase === AUTHORIZATION_PHASES.IDLE
    || state.phase === AUTHORIZATION_PHASES.REJECTED
    || state.phase === AUTHORIZATION_PHASES.EXPIRED) {
    return state;
  }

  return { ...state, phase: AUTHORIZATION_PHASES.EXPIRED, token: null };
}

// Retry = solicitud nueva (S5): solo desde "Expirada"; cualquier otro estado queda intacto.
export function retryAuthorizationWait(state, request) {
  if (!state || state.phase !== AUTHORIZATION_PHASES.EXPIRED) return state;
  return startAuthorizationWait(createAuthorizationFlowState(), request);
}

export function cancelAuthorizationWait() {
  return createAuthorizationFlowState();
}

// 8.151 (W2, R5/design D5): causas de liquidacion de la promesa del caller. El reducer expone
// el resultado exacto; el provider decide el momento (el rechazo liquida al instante, la
// expirada solo al desestimarse sin reintento) y el cierre del modal de acuse.
export const AUTHORIZATION_SETTLEMENTS = Object.freeze({
  REJECTION: 'rejection',
  EXPIRED_DISMISSAL: 'expired-dismissal',
  MANUAL_CANCEL: 'manual-cancel',
});

/**
 * 8.151 (W2, R5/design D5): liquida el resultado que recibe el caller segun la fase alcanzada.
 * - rejection: { ok:false, outcome:'rejected', reason, resolvedByName } (acuse abierto)
 * - expired-dismissal: { ok:false, outcome:'expired' } (desestimar sin reintentar)
 * - manual-cancel: { ok:false, cancelled:true } (cancelacion desde la espera)
 * Devuelve null cuando la fase no corresponde al trigger (sin liquidacion pendiente).
 */
export function getAuthorizationSettlement(state, trigger) {
  if (!state) return null;

  if (trigger === AUTHORIZATION_SETTLEMENTS.REJECTION
    && state.phase === AUTHORIZATION_PHASES.REJECTED) {
    return {
      ok: false,
      outcome: 'rejected',
      reason: state.reason ?? null,
      resolvedByName: state.resolvedByName ?? null,
    };
  }

  if (trigger === AUTHORIZATION_SETTLEMENTS.EXPIRED_DISMISSAL
    && state.phase === AUTHORIZATION_PHASES.EXPIRED) {
    return { ok: false, outcome: 'expired' };
  }

  if (trigger === AUTHORIZATION_SETTLEMENTS.MANUAL_CANCEL
    && state.phase === AUTHORIZATION_PHASES.WAITING) {
    return { ok: false, cancelled: true };
  }

  return null;
}

// 8.151 (W2, R4-client): el retiro server-side solo aplica mientras la solicitud sigue Pending;
// el formulario de autorizacion local es una sub-vista de waiting (mismo requestId vivo).
export function isAuthorizationPending(state) {
  return Boolean(state) && state.phase === AUTHORIZATION_PHASES.WAITING && state.requestId != null;
}

export function getRemainingSeconds(expiresAt, now = Date.now()) {
  if (!expiresAt) return 0;
  const target = new Date(expiresAt).getTime();
  if (!Number.isFinite(target)) return 0;
  const diff = target - now;
  if (diff <= 0) return 0;
  // 8.151 (W2, R7/design D4c): piso, nunca techo: el display no muestra 00:01 con <1s vivo.
  return Math.floor(diff / 1000);
}

export function isAuthorizationExpired(expiresAt, now = Date.now()) {
  if (!expiresAt) return false;
  return getRemainingSeconds(expiresAt, now) <= 0;
}