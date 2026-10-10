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

export function getRemainingSeconds(expiresAt, now = Date.now()) {
  if (!expiresAt) return 0;
  const target = new Date(expiresAt).getTime();
  if (!Number.isFinite(target)) return 0;
  const diff = target - now;
  if (diff <= 0) return 0;
  return Math.ceil(diff / 1000);
}

export function isAuthorizationExpired(expiresAt, now = Date.now()) {
  if (!expiresAt) return false;
  return getRemainingSeconds(expiresAt, now) <= 0;
}