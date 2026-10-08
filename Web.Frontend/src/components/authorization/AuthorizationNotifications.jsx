import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { Check, ShieldAlert, Timer, X } from 'lucide-react';
import { registerOpenModal } from '../../utils/modalRegistry';
import { AUTHORIZATION_EVENT_NAMES, authorizationHub } from '../../services/authorizationHub.js';
import { getRemainingSeconds } from '../../services/authorizationFlow.js';
import './AuthorizationNotifications.css';

// 8.150 (T8, design D7): textos de la notificacion admin. El mensaje de carrera es el formato
// exacto de la spec "Esta solicitud ya fue resuelta por {resolverName}."; el backend puede
// enviarlo ya formado y el cliente lo prefiere (ver applyResolveAuthorizationOutcome).
export const AUTHORIZATION_NOTIFICATION_STRINGS = Object.freeze({
  title: 'Solicitud de autorización',
  approve: 'Aprobar',
  reject: 'Rechazar',
  reasonLabel: 'Motivo (opcional)',
  reasonPlaceholder: 'Motivo (opcional)',
  terminal: 'Terminal',
  remaining: 'Tiempo restante',
  expired: 'Solicitud expirada.',
  pendingCount: 'solicitudes pendientes',
  dismiss: 'Cerrar',
  resolveFailed: 'No se pudo resolver la solicitud de autorización.',
  fallbackCashier: 'otro usuario',
});

export const AUTHORIZATION_ACTION_LABELS = Object.freeze({
  ManualPriceOverride: 'modificar el precio manual',
});

function formatCountdown(totalSeconds) {
  const safe = Math.max(0, Math.floor(Number(totalSeconds) || 0));
  const minutes = String(Math.floor(safe / 60)).padStart(2, '0');
  const seconds = String(safe % 60).padStart(2, '0');
  return `${minutes}:${seconds}`;
}

function buildAuthorizationActionLabel(actionType) {
  if (!actionType) return 'una acción protegida';
  return AUTHORIZATION_ACTION_LABELS[actionType] ?? actionType;
}

export function buildAuthorizationDetail(context) {
  if (!context || typeof context !== 'object') return '';

  const parts = [];
  if (context.productName) {
    parts.push(context.quantity != null
      ? `Producto: ${context.productName} × ${context.quantity}`
      : `Producto: ${context.productName}`);
  }
  if (context.customUnitPriceUsd != null) {
    parts.push(`Precio USD: $ ${Number(context.customUnitPriceUsd).toFixed(2)}`);
  }
  if (context.customUnitPriceLocal != null) {
    parts.push(`Precio Bs.S: ${Number(context.customUnitPriceLocal).toFixed(2)}`);
  }
  return parts.join('. ');
}

export function buildAuthorizationNotificationMessage(request) {
  const cashier = request?.requestedByName;
  const subject = cashier ? `El cajero ${cashier}` : 'Un cajero';
  const action = buildAuthorizationActionLabel(request?.actionType);
  const saleClause = request?.saleId != null ? ` en la Factura #${request.saleId}` : '';
  const base = `${subject} solicita autorización para ${action}${saleClause}.`;
  const detail = buildAuthorizationDetail(request?.context);
  return detail ? `${base} ${detail}` : base;
}

export function buildAlreadyResolvedMessage(resolverName) {
  return `Esta solicitud ya fue resuelta por ${resolverName || AUTHORIZATION_NOTIFICATION_STRINGS.fallbackCashier}.`;
}

// 8.150 (T8): reducer puro de la cola; cada evento del hub conserva las solicitudes restantes.
export function enqueueAuthorizationNotification(queue, payload) {
  if (!payload || payload.requestId == null) return queue;
  if (queue.some((item) => item.requestId === payload.requestId)) return queue;

  return [...queue, {
    requestId: payload.requestId,
    actionType: payload.actionType ?? null,
    saleId: payload.saleId ?? null,
    requestedByName: payload.requestedByName ?? null,
    terminal: payload.terminal ?? null,
    context: payload.context ?? null,
    createdAt: payload.createdAt ?? null,
    expiresAt: payload.expiresAt ?? null,
    message: buildAuthorizationNotificationMessage(payload),
  }];
}

export function removeAuthorizationNotification(queue, requestId) {
  return queue.filter((item) => item.requestId !== requestId);
}

export function applyAuthorizationResolvedEvent(queue, payload, { isOwnResolution = false } = {}) {
  const requestId = payload?.requestId;
  const wasQueued = queue.some((item) => item.requestId === requestId);
  const nextQueue = removeAuthorizationNotification(queue, requestId);

  if (!wasQueued || isOwnResolution) return { queue: nextQueue, notice: null };
  return { queue: nextQueue, notice: buildAlreadyResolvedMessage(payload?.resolvedByName) };
}

export function applyAuthorizationExpiredEvent(queue, payload) {
  const nextQueue = removeAuthorizationNotification(queue, payload?.requestId);
  const wasQueued = nextQueue.length !== queue.length;
  return {
    queue: nextQueue,
    notice: wasQueued ? AUTHORIZATION_NOTIFICATION_STRINGS.expired : null,
  };
}

export function applyResolveAuthorizationOutcome(queue, { requestId, success, message = null, resolvedByName = null } = {}) {
  const nextQueue = removeAuthorizationNotification(queue, requestId);
  if (success) return { queue: nextQueue, notice: null };

  const notice = message || (resolvedByName ? buildAlreadyResolvedMessage(resolvedByName) : null);
  return { queue: nextQueue, notice };
}

export function AuthorizationNotificationNotice({ notice = null, onDismiss = null }) {
  if (!notice) return null;

  return (
    <div className="an-notice" role="alert">
      <ShieldAlert size={18} />
      <span className="an-notice-message">{notice}</span>
      {onDismiss && (
        <button
          type="button"
          className="an-notice-dismiss"
          onClick={onDismiss}
          aria-label={AUTHORIZATION_NOTIFICATION_STRINGS.dismiss}
        >
          <X size={16} />
        </button>
      )}
    </div>
  );
}

export function AuthorizationNotificationModal({
  notification = null,
  queuedCount = 0,
  remainingSeconds = 0,
  isSubmitting = false,
  error = null,
  onApprove,
  onReject,
}) {
  const modalRef = useRef(null);
  const titleId = useId();
  const [reason, setReason] = useState('');
  const requestId = notification?.requestId ?? null;

  useEffect(() => {
    setReason('');
  }, [requestId]);

  useEffect(() => {
    if (!notification) return undefined;
    return registerOpenModal();
  }, [notification]);

  useEffect(() => {
    if (!notification) return undefined;

    function handleKeyDown(e) {
      if (e.key !== 'Tab' || !modalRef.current) return;

      const focusable = modalRef.current.querySelectorAll(
        'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
      );
      if (focusable.length === 0) return;

      const first = focusable[0];
      const last = focusable[focusable.length - 1];

      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [notification]);

  if (!notification) return null;

  const handleReject = () => {
    const trimmed = reason.trim();
    onReject?.(trimmed || null);
  };

  return (
    <div className="modal-overlay an-overlay">
      <div
        ref={modalRef}
        className="modal-container card an-container"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
      >
        <div className="an-icon-row">
          <div className="an-icon-circle">
            <ShieldAlert size={26} />
          </div>
        </div>

        <h3 id={titleId} className="an-title">{AUTHORIZATION_NOTIFICATION_STRINGS.title}</h3>

        {queuedCount > 1 && (
          <div className="an-queue-badge">
            {`${queuedCount} ${AUTHORIZATION_NOTIFICATION_STRINGS.pendingCount}`}
          </div>
        )}

        <p className="an-message">{notification.message}</p>

        <div className="an-meta">
          {notification.terminal && (
            <div className="an-meta-row">
              <span className="an-meta-label">{AUTHORIZATION_NOTIFICATION_STRINGS.terminal}</span>
              <span className="an-meta-value">{notification.terminal}</span>
            </div>
          )}
          {notification.expiresAt && (
            <div className="an-meta-row">
              <span className="an-meta-label">
                <Timer size={16} /> {AUTHORIZATION_NOTIFICATION_STRINGS.remaining}
              </span>
              <span className="an-countdown">{formatCountdown(remainingSeconds)}</span>
            </div>
          )}
        </div>

        {error && <div className="an-error" role="alert">{error}</div>}

        <label className="an-field">
          <span className="an-field-label">{AUTHORIZATION_NOTIFICATION_STRINGS.reasonLabel}</span>
          <input
            type="text"
            className="an-input"
            placeholder={AUTHORIZATION_NOTIFICATION_STRINGS.reasonPlaceholder}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            disabled={isSubmitting}
          />
        </label>

        <div className="an-actions">
          <button
            type="button"
            className="btn an-btn an-btn-approve"
            onClick={() => onApprove?.()}
            disabled={isSubmitting}
          >
            <Check size={18} /> {AUTHORIZATION_NOTIFICATION_STRINGS.approve}
          </button>
          <button
            type="button"
            className="btn an-btn an-btn-reject"
            onClick={handleReject}
            disabled={isSubmitting}
          >
            <X size={18} /> {AUTHORIZATION_NOTIFICATION_STRINGS.reject}
          </button>
        </div>

        {isSubmitting && <div className="an-spinner" role="status" aria-live="polite" />}
      </div>
    </div>
  );
}

/**
 * 8.150 (T8, design D7): cola de notificaciones admin. Interrumpe con la solicitud mas antigua,
 * resuelve por hub con fallback REST y cierra el modal en cuanto llega la resolucion remota.
 * `onOpenChange` expone el gating de hotkeys al contenedor App (precedente del modal de espera).
 */
export default function AuthorizationNotifications({ onOpenChange = null }) {
  const [queue, setQueue] = useState([]);
  const [notice, setNotice] = useState(null);
  const [resolveError, setResolveError] = useState(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [now, setNow] = useState(() => Date.now());

  const queueRef = useRef([]);
  const pendingResolutionRef = useRef(null);
  const ownResolvedRef = useRef(new Set());

  const commitQueue = useCallback((updater) => {
    const next = typeof updater === 'function' ? updater(queueRef.current) : updater;
    queueRef.current = next;
    setQueue(next);
  }, []);

  const handleRequested = useCallback((payload) => {
    commitQueue((current) => enqueueAuthorizationNotification(current, payload));
  }, [commitQueue]);

  const handleResolved = useCallback((payload) => {
    const isOwnResolution = pendingResolutionRef.current === payload?.requestId
      || ownResolvedRef.current.has(payload?.requestId);
    const result = applyAuthorizationResolvedEvent(queueRef.current, payload, { isOwnResolution });
    commitQueue(result.queue);
    if (result.notice) setNotice(result.notice);
  }, [commitQueue]);

  const handleExpired = useCallback((payload) => {
    const result = applyAuthorizationExpiredEvent(queueRef.current, payload);
    commitQueue(result.queue);
    if (result.notice) setNotice(result.notice);
  }, [commitQueue]);

  const reconcileQueue = useCallback(async () => {
    for (const item of [...queueRef.current]) {
      let status;
      try {
        status = await authorizationHub.getStatus(item.requestId);
      } catch (err) {
        if (err?.status === 404) handleExpired({ requestId: item.requestId });
        continue;
      }

      if (status?.status === 'Approved' || status?.status === 'Rejected') {
        handleResolved({
          requestId: item.requestId,
          status: status.status,
          resolvedByName: status.resolvedByName ?? null,
        });
      } else if (status?.status === 'Expired'
        || (status?.expiresAt && getRemainingSeconds(status.expiresAt) <= 0)) {
        handleExpired({ requestId: item.requestId });
      }
    }
  }, [handleExpired, handleResolved]);

  useEffect(() => {
    const offRequested = authorizationHub.on(AUTHORIZATION_EVENT_NAMES.requested, handleRequested);
    const offResolved = authorizationHub.on(AUTHORIZATION_EVENT_NAMES.resolved, handleResolved);
    const offExpired = authorizationHub.on(AUTHORIZATION_EVENT_NAMES.expired, handleExpired);
    const offReconnected = authorizationHub.onReconnected(reconcileQueue);
    authorizationHub.connect();
    return () => {
      offRequested();
      offResolved();
      offExpired();
      offReconnected();
    };
  }, [handleRequested, handleResolved, handleExpired, reconcileQueue]);

  useEffect(() => {
    if (queue.length === 0) return undefined;

    const interval = setInterval(() => {
      const timestamp = Date.now();
      const survivors = queueRef.current.filter(
        (item) => !item.expiresAt || getRemainingSeconds(item.expiresAt, timestamp) > 0
      );
      if (survivors.length !== queueRef.current.length) {
        commitQueue(survivors);
        setNotice(AUTHORIZATION_NOTIFICATION_STRINGS.expired);
      }
      setNow(timestamp);
    }, 500);

    return () => clearInterval(interval);
  }, [queue.length, commitQueue]);

  useEffect(() => {
    onOpenChange?.(queue.length > 0);
  }, [queue.length, onOpenChange]);

  useEffect(() => () => {
    onOpenChange?.(false);
  }, [onOpenChange]);

  useEffect(() => {
    if (!notice) return undefined;
    const timer = setTimeout(() => setNotice(null), 8000);
    return () => clearTimeout(timer);
  }, [notice]);

  const submitResolution = useCallback(async (approved, reason = null) => {
    const head = queueRef.current[0];
    if (!head || pendingResolutionRef.current !== null) return;

    const requestId = head.requestId;
    pendingResolutionRef.current = requestId;
    setIsSubmitting(true);
    setResolveError(null);

    try {
      await authorizationHub.resolveAuthorization(requestId, approved, reason);
      ownResolvedRef.current.add(requestId);
      commitQueue((current) => removeAuthorizationNotification(current, requestId));
    } catch (err) {
      if (err?.status === 409 || err?.status === 404) {
        const outcome = applyResolveAuthorizationOutcome(queueRef.current, {
          requestId,
          success: false,
          message: err?.message ?? null,
          resolvedByName: err?.body?.resolvedByName ?? null,
        });
        commitQueue(outcome.queue);
        setNotice(outcome.notice || AUTHORIZATION_NOTIFICATION_STRINGS.resolveFailed);
      } else {
        setResolveError(err?.message || AUTHORIZATION_NOTIFICATION_STRINGS.resolveFailed);
      }
    } finally {
      pendingResolutionRef.current = null;
      setIsSubmitting(false);
    }
  }, [commitQueue]);

  const approveHead = useCallback(() => submitResolution(true, null), [submitResolution]);
  const rejectHead = useCallback((reason) => submitResolution(false, reason), [submitResolution]);

  const head = queue.length > 0 ? queue[0] : null;
  const remainingSeconds = head ? getRemainingSeconds(head.expiresAt, now) : 0;

  return (
    <>
      <AuthorizationNotificationNotice notice={notice} onDismiss={() => setNotice(null)} />
      <AuthorizationNotificationModal
        notification={head}
        queuedCount={queue.length}
        remainingSeconds={remainingSeconds}
        isSubmitting={isSubmitting}
        error={resolveError}
        onApprove={approveHead}
        onReject={rejectHead}
      />
    </>
  );
}
