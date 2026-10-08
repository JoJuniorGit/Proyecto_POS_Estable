import { useEffect, useId, useRef, useState } from 'react';
import { Lock, ShieldAlert, Timer, User } from 'lucide-react';
import { registerOpenModal } from '../../utils/modalRegistry';
import { AUTHORIZATION_PHASES, AUTHORIZATION_STRINGS } from '../../services/authorizationFlow.js';
import './AuthorizationWaitModal.css';

function formatCountdown(totalSeconds) {
  const safe = Math.max(0, Math.floor(Number(totalSeconds) || 0));
  const minutes = String(Math.floor(safe / 60)).padStart(2, '0');
  const seconds = String(safe % 60).padStart(2, '0');
  return `${minutes}:${seconds}`;
}

/**
 * 8.150 (T7, design D7): modal bloqueante del flujo de espera. Recibe todo por props desde
 * AuthorizationProvider para poder validarse estructuralmente sin DOM (renderToString).
 */
export default function AuthorizationWaitModal({
  isOpen,
  phase = AUTHORIZATION_PHASES.WAITING,
  context = null,
  remainingSeconds = 0,
  reason = null,
  retryError = null,
  isLocalFormOpen = false,
  localError = null,
  isSubmitting = false,
  onOpenLocalForm,
  onCloseLocalForm,
  onLocalSubmit,
  onCancel,
  onRetry,
  onRetryExecution,
}) {
  const modalRef = useRef(null);
  const titleId = useId();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');

  useEffect(() => {
    if (!isOpen) return undefined;
    return registerOpenModal();
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return undefined;

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
  }, [isOpen]);

  if (!isOpen) return null;

  const isExecuting = phase === AUTHORIZATION_PHASES.GRANTED || phase === AUTHORIZATION_PHASES.LOCAL;
  const title = phase === AUTHORIZATION_PHASES.REJECTED
    ? AUTHORIZATION_STRINGS.rejected
    : phase === AUTHORIZATION_PHASES.EXPIRED
      ? AUTHORIZATION_STRINGS.expired
      : AUTHORIZATION_STRINGS.waiting;

  const handleLocalSubmit = (e) => {
    e.preventDefault();
    if (!username.trim() || !password) return;
    onLocalSubmit?.({ username: username.trim(), password });
  };

  return (
    <div className="modal-overlay awm-overlay" onClick={onCancel}>
      <div
        ref={modalRef}
        className="modal-container card awm-container"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="awm-icon-row">
          <div className={`awm-icon-circle${phase === AUTHORIZATION_PHASES.REJECTED ? ' awm-icon-danger' : ''}`}>
            <ShieldAlert size={28} />
          </div>
        </div>

        <h3 id={titleId} className="awm-title">{title}</h3>

        {phase === AUTHORIZATION_PHASES.WAITING && (
          <div className="awm-countdown">
            <Timer size={18} />
            <span className="awm-countdown-value">{formatCountdown(remainingSeconds)}</span>
          </div>
        )}

        {context && (
          <div className="awm-context">
            {context.saleId != null && (
              <div className="awm-context-row">
                <span className="awm-context-label">Factura</span>
                <span className="awm-context-value">{`#${context.saleId}`}</span>
              </div>
            )}
            {context.productName && (
              <div className="awm-context-row">
                <span className="awm-context-label">Producto</span>
                <span className="awm-context-value">
                  {context.quantity != null ? `${context.productName} × ${context.quantity}` : context.productName}
                </span>
              </div>
            )}
            {context.customUnitPriceUsd != null && (
              <div className="awm-context-row">
                <span className="awm-context-label">Precio USD</span>
                <span className="awm-context-value">{`$ ${Number(context.customUnitPriceUsd).toFixed(2)}`}</span>
              </div>
            )}
            {context.customUnitPriceLocal != null && (
              <div className="awm-context-row">
                <span className="awm-context-label">Precio Bs.S</span>
                <span className="awm-context-value">{`Bs.S ${Number(context.customUnitPriceLocal).toFixed(2)}`}</span>
              </div>
            )}
          </div>
        )}

        {phase === AUTHORIZATION_PHASES.REJECTED && reason && (
          <p className="awm-reason">{reason}</p>
        )}

        {phase === AUTHORIZATION_PHASES.WAITING && !isLocalFormOpen && (
          <div className="awm-actions">
            <button type="button" className="btn awm-btn awm-btn-local" onClick={onOpenLocalForm}>
              {AUTHORIZATION_STRINGS.localAuthorization}
            </button>
            <button type="button" className="btn btn-outline awm-btn" onClick={onCancel}>
              {AUTHORIZATION_STRINGS.cancel}
            </button>
          </div>
        )}

        {phase === AUTHORIZATION_PHASES.WAITING && isLocalFormOpen && (
          <form className="awm-local-form" onSubmit={handleLocalSubmit}>
            {localError && <div className="awm-error" role="alert">{localError}</div>}

            <label className="awm-field">
              <span className="awm-field-label">
                <User size={16} /> {AUTHORIZATION_STRINGS.username}
              </span>
              <input
                type="text"
                className="awm-input"
                placeholder={AUTHORIZATION_STRINGS.username}
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                disabled={isSubmitting}
                autoFocus
              />
            </label>

            <label className="awm-field">
              <span className="awm-field-label">
                <Lock size={16} /> {AUTHORIZATION_STRINGS.password}
              </span>
              <input
                type="password"
                className="awm-input"
                placeholder={AUTHORIZATION_STRINGS.password}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                disabled={isSubmitting}
              />
            </label>

            <div className="awm-actions">
              <button
                type="submit"
                className="btn awm-btn awm-btn-local"
                disabled={isSubmitting || !username.trim() || !password}
              >
                {AUTHORIZATION_STRINGS.authorize}
              </button>
              <button type="button" className="btn btn-outline awm-btn" onClick={onCloseLocalForm}>
                {AUTHORIZATION_STRINGS.cancel}
              </button>
            </div>
          </form>
        )}

        {phase === AUTHORIZATION_PHASES.EXPIRED && (
          <div className="awm-actions awm-actions-stacked">
            {retryError && <div className="awm-error" role="alert">{retryError}</div>}
            <button type="button" className="btn awm-btn awm-btn-local" onClick={onRetry}>
              {AUTHORIZATION_STRINGS.retry}
            </button>
            <button type="button" className="btn btn-outline awm-btn" onClick={onCancel}>
              {AUTHORIZATION_STRINGS.cancel}
            </button>
          </div>
        )}

        {phase === AUTHORIZATION_PHASES.REJECTED && (
          <div className="awm-actions">
            <button type="button" className="btn btn-outline awm-btn" onClick={onCancel}>
              {AUTHORIZATION_STRINGS.cancel}
            </button>
          </div>
        )}

        {isExecuting && !retryError && (
          <div className="awm-spinner" role="status" aria-live="polite" />
        )}

        {isExecuting && retryError && (
          <div className="awm-actions awm-actions-stacked">
            <div className="awm-error" role="alert">{retryError}</div>
            <button type="button" className="btn awm-btn awm-btn-local" onClick={onRetryExecution}>
              {AUTHORIZATION_STRINGS.retry}
            </button>
            <button type="button" className="btn btn-outline awm-btn" onClick={onCancel}>
              {AUTHORIZATION_STRINGS.cancel}
            </button>
          </div>
        )}
      </div>
    </div>
  );
}