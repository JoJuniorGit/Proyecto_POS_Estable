import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import {
  AUTHORIZATION_EVENT_NAMES,
  authorizationHub,
  buildAuthorizationRequestPayload,
} from '../../services/authorizationHub.js';
import {
  AUTHORIZATION_PHASES,
  AUTHORIZATION_STRINGS,
  applyAuthorizationExpiry,
  applyAuthorizationResolution,
  cancelAuthorizationWait,
  createAuthorizationFlowState,
  getRemainingSeconds,
  retryAuthorizationWait,
  startAuthorizationWait,
} from '../../services/authorizationFlow.js';
import AuthorizationWaitModal from './AuthorizationWaitModal.jsx';

const AuthorizationContext = createContext(null);

/**
 * 8.150 (T7, design D7): provee el flujo de espera bloqueante como infraestructura.
 * v1 no tiene disparador web (veto L6); una accion protegida futura llama
 * startWait({ context, retry }) y recibe el resultado; el modal vive aqui.
 */
export function AuthorizationProvider({ children }) {
  const [flow, setFlow] = useState(createAuthorizationFlowState);
  const [remainingSeconds, setRemainingSeconds] = useState(0);
  const [isLocalFormOpen, setIsLocalFormOpen] = useState(false);
  const [localError, setLocalError] = useState(null);
  const [isSubmittingLocal, setIsSubmittingLocal] = useState(false);
  const [retryError, setRetryError] = useState(null);

  const flowRef = useRef(flow);
  const pendingRef = useRef(null);
  const executionRef = useRef(null);
  const isExecutingRef = useRef(false);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const commitFlow = useCallback((next) => {
    flowRef.current = next;
    if (mountedRef.current) setFlow(next);
  }, []);

  const expireWait = useCallback(() => {
    if (!pendingRef.current || !pendingRef.current.resolve) return;
    if (flowRef.current.phase !== AUTHORIZATION_PHASES.WAITING) return;
    commitFlow(applyAuthorizationExpiry(flowRef.current));
    setIsLocalFormOpen(false);
    setLocalError(null);
  }, [commitFlow]);

  const executeRetry = useCallback(async (token, resolutionMode, resolvedByName) => {
    const current = pendingRef.current;
    if (!current || !current.resolve) return;
    // Una sola ejecucion por concesion: push + local-resolve + recovery pueden competir
    // por el mismo grant y no deben re-invocar la accion del caller dos veces.
    if (isExecutingRef.current) return;

    if (!token) {
      executionRef.current = null;
      commitFlow(applyAuthorizationExpiry(flowRef.current));
      setRetryError(null);
      return;
    }

    isExecutingRef.current = true;
    executionRef.current = { token, resolutionMode, resolvedByName };
    const requestId = flowRef.current.requestId;
    setRetryError(null);

    try {
      await current.retry(token);
      pendingRef.current = null;
      executionRef.current = null;
      commitFlow(createAuthorizationFlowState());
      setIsLocalFormOpen(false);
      current.resolve({ ok: true, requestId, token, resolutionMode, resolvedByName });
    } catch (err) {
      // Token ya consumido o ejecucion fallida: se conserva el token para reintentar
      // (el endpoint replay-aware resuelve la idempotencia) y se muestra el error real.
      setRetryError(err?.message || String(err));
    } finally {
      isExecutingRef.current = false;
    }
  }, [commitFlow]);

  const recoverStatus = useCallback(async () => {
    const current = pendingRef.current;
    if (!current || !current.resolve || flowRef.current.requestId == null) return false;

    let status;
    try {
      status = await authorizationHub.getStatus(flowRef.current.requestId);
    } catch (err) {
      if (err?.status === 404) {
        expireWait();
        return true;
      }
      return false;
    }
    if (!status) return false;

    if (status.status === 'Approved') {
      const token = status.token ?? null;
      const resolutionMode = status.resolutionMode || 'Remote';
      commitFlow(applyAuthorizationResolution(flowRef.current, {
        approved: true,
        token,
        resolutionMode,
        resolvedByName: status.resolvedByName ?? null,
      }));
      setIsLocalFormOpen(false);
      setIsSubmittingLocal(false);
      await executeRetry(token, resolutionMode, status.resolvedByName ?? null);
      return true;
    }

    if (status.status === 'Rejected') {
      commitFlow(applyAuthorizationResolution(flowRef.current, {
        approved: false,
        reason: status.reason ?? null,
        resolvedByName: status.resolvedByName ?? null,
      }));
      setIsLocalFormOpen(false);
      return true;
    }

    if (status.status === 'Expired') {
      expireWait();
      return true;
    }

    if (status.status === 'Pending' && status.expiresAt) {
      if (getRemainingSeconds(status.expiresAt) <= 0) {
        expireWait();
        return true;
      }
      commitFlow(startAuthorizationWait(flowRef.current, {
        requestId: status.id ?? flowRef.current.requestId,
        expiresAt: status.expiresAt,
        context: flowRef.current.context,
        deduplicated: flowRef.current.deduplicated,
      }));
      return true;
    }

    return false;
  }, [commitFlow, executeRetry, expireWait]);

  const applyResolvedPayload = useCallback((payload) => {
    const current = pendingRef.current;
    if (!current || !current.resolve || !payload) return;
    if (Number(payload.requestId) !== flowRef.current.requestId) return;

    const approved = payload.approved === true || payload.status === 'Approved';
    if (!approved) {
      commitFlow(applyAuthorizationResolution(flowRef.current, {
        approved: false,
        reason: payload.reason ?? null,
        resolvedByName: payload.resolvedByName ?? null,
      }));
      setIsLocalFormOpen(false);
      setLocalError(null);
      return;
    }

    const resolutionMode = payload.resolutionMode || 'Remote';
    commitFlow(applyAuthorizationResolution(flowRef.current, {
      approved: true,
      token: payload.token ?? null,
      resolutionMode,
      resolvedByName: payload.resolvedByName ?? null,
    }));
    setIsLocalFormOpen(false);
    setLocalError(null);
    executeRetry(payload.token ?? null, resolutionMode, payload.resolvedByName ?? null);
  }, [commitFlow, executeRetry]);

  const applyExpiredPayload = useCallback((payload) => {
    const current = pendingRef.current;
    if (!current || !current.resolve || !payload) return;
    if (Number(payload.requestId) !== flowRef.current.requestId) return;
    expireWait();
  }, [expireWait]);

  useEffect(() => {
    const offResolved = authorizationHub.on(AUTHORIZATION_EVENT_NAMES.resolved, applyResolvedPayload);
    const offExpired = authorizationHub.on(AUTHORIZATION_EVENT_NAMES.expired, applyExpiredPayload);
    const offReconnected = authorizationHub.onReconnected(recoverStatus);
    return () => {
      offResolved();
      offExpired();
      offReconnected();
    };
  }, [applyResolvedPayload, applyExpiredPayload, recoverStatus]);

  useEffect(() => {
    if (flow.phase !== AUTHORIZATION_PHASES.WAITING) return undefined;

    const tick = () => {
      const seconds = getRemainingSeconds(flow.expiresAt);
      setRemainingSeconds(seconds);
      if (seconds <= 0 && flow.expiresAt) {
        expireWait();
      }
    };

    tick();
    const interval = setInterval(tick, 500);
    return () => clearInterval(interval);
  }, [flow.phase, flow.expiresAt, expireWait]);

  const startWait = useCallback(async ({ context, retry } = {}) => {
    if (!context || typeof retry !== 'function') {
      throw new Error('startWait requiere { context, retry }.');
    }
    if (pendingRef.current) {
      throw new Error('Ya existe una espera de autorización en curso.');
    }

    pendingRef.current = { pending: true };
    let request;
    try {
      request = await authorizationHub.requestAuthorization(buildAuthorizationRequestPayload(context));
    } catch (err) {
      pendingRef.current = null;
      throw err;
    }

    if (!request || request.requestId == null) {
      pendingRef.current = null;
      throw new Error('La solicitud de autorización no devolvió un identificador.');
    }

    commitFlow(startAuthorizationWait(createAuthorizationFlowState(), {
      requestId: request.requestId,
      expiresAt: request.expiresAt,
      context,
      deduplicated: request.deduplicated,
    }));
    setLocalError(null);
    setIsLocalFormOpen(false);
    setRetryError(null);
    setRemainingSeconds(getRemainingSeconds(request.expiresAt));

    return new Promise((resolve) => {
      pendingRef.current = { resolve, context, retry };
      // Recuperacion inmediata: si la resolucion se emitio antes de registrar el requestId,
      // el GET de estado cierra la ventana de push perdido sin esperar al reconnect.
      recoverStatus();
    });
  }, [commitFlow, recoverStatus]);

  const cancelWait = useCallback(() => {
    const current = pendingRef.current;
    if (!current || !current.resolve) return false;

    const phase = flowRef.current.phase;
    if (phase === AUTHORIZATION_PHASES.GRANTED || phase === AUTHORIZATION_PHASES.LOCAL) {
      // Ya existe autorizacion y la accion se esta reanudando: cancelar no debe abortarla.
      return false;
    }

    pendingRef.current = null;
    executionRef.current = null;
    commitFlow(cancelAuthorizationWait());
    setIsLocalFormOpen(false);
    setLocalError(null);
    setRetryError(null);
    current.resolve({ ok: false, cancelled: true });
    return true;
  }, [commitFlow]);

  const retryWait = useCallback(async () => {
    const current = pendingRef.current;
    if (!current || !current.resolve) return false;
    if (flowRef.current.phase !== AUTHORIZATION_PHASES.EXPIRED) return false;

    try {
      const request = await authorizationHub.requestAuthorization(buildAuthorizationRequestPayload(current.context));
      const next = retryAuthorizationWait(flowRef.current, {
        requestId: request.requestId,
        expiresAt: request.expiresAt,
        context: current.context,
        deduplicated: request.deduplicated,
      });
      commitFlow(next);
      setRetryError(null);
      setLocalError(null);
      setIsLocalFormOpen(false);
      // Mismo cierre de la ventana push-antes-de-registro que startWait.
      recoverStatus();
      return true;
    } catch (err) {
      setRetryError(err?.message || String(err));
      return false;
    }
  }, [commitFlow, recoverStatus]);

  const openLocalForm = useCallback(() => {
    setLocalError(null);
    setIsLocalFormOpen(true);
  }, []);

  const closeLocalForm = useCallback(() => {
    setLocalError(null);
    setIsLocalFormOpen(false);
  }, []);

  const submitLocal = useCallback(async ({ username, password } = {}) => {
    const current = pendingRef.current;
    if (!current || !current.resolve || flowRef.current.requestId == null) return false;

    setIsSubmittingLocal(true);
    setLocalError(null);
    try {
      const result = await authorizationHub.localResolve(flowRef.current.requestId, {
        username,
        password,
        reason: null,
      });
      const resolutionMode = 'Local';
      commitFlow(applyAuthorizationResolution(flowRef.current, {
        approved: true,
        token: result.token ?? null,
        resolutionMode,
        resolvedByName: result.supervisorName ?? null,
      }));
      setIsLocalFormOpen(false);
      await executeRetry(result.token ?? null, resolutionMode, result.supervisorName ?? null);
      return true;
    } catch (err) {
      if (err?.status === 409) {
        const recovered = await recoverStatus();
        if (recovered) return true;
      }
      setLocalError(err?.message || AUTHORIZATION_STRINGS.invalidCredentials);
      return false;
    } finally {
      setIsSubmittingLocal(false);
    }
  }, [commitFlow, executeRetry, recoverStatus]);

  const retryExecution = useCallback(() => {
    const execution = executionRef.current;
    if (!execution) return;
    executeRetry(execution.token, execution.resolutionMode, execution.resolvedByName);
  }, [executeRetry]);

  const value = useMemo(() => ({
    isWaitOpen: flow.phase !== AUTHORIZATION_PHASES.IDLE,
    phase: flow.phase,
    context: flow.context,
    requestId: flow.requestId,
    expiresAt: flow.expiresAt,
    remainingSeconds,
    reason: flow.reason,
    resolutionMode: flow.resolutionMode,
    resolvedByName: flow.resolvedByName,
    retryError,
    isLocalFormOpen,
    localError,
    isSubmittingLocal,
    startWait,
    cancelWait,
    retryWait,
    openLocalForm,
    closeLocalForm,
    submitLocal,
    retryExecution,
  }), [
    flow,
    remainingSeconds,
    retryError,
    isLocalFormOpen,
    localError,
    isSubmittingLocal,
    startWait,
    cancelWait,
    retryWait,
    openLocalForm,
    closeLocalForm,
    submitLocal,
    retryExecution,
  ]);

  return (
    <AuthorizationContext.Provider value={value}>
      {children}
      <AuthorizationWaitModal
        isOpen={value.isWaitOpen}
        phase={flow.phase}
        context={flow.context}
        remainingSeconds={remainingSeconds}
        reason={flow.reason}
        retryError={retryError}
        isLocalFormOpen={isLocalFormOpen}
        localError={localError}
        isSubmitting={isSubmittingLocal}
        onOpenLocalForm={openLocalForm}
        onCloseLocalForm={closeLocalForm}
        onLocalSubmit={submitLocal}
        onCancel={cancelWait}
        onRetry={retryWait}
        onRetryExecution={retryExecution}
      />
    </AuthorizationContext.Provider>
  );
}

export function useAuthorization() {
  const context = useContext(AuthorizationContext);
  if (!context) {
    throw new Error('useAuthorization debe ser usado dentro de un AuthorizationProvider');
  }
  return context;
}