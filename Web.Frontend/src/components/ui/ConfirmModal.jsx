import { useEffect, useId, useRef } from 'react';
import { AlertTriangle } from 'lucide-react';
import { registerOpenModal } from '../../utils/modalRegistry';
import './ConfirmModal.css';

/**
 * ConfirmModal
 *
 * Diálogo de confirmación personalizado para reemplazar window.confirm.
 * Diseñado con tokens CSS del POS (modo claro y oscuro), accesible mediante teclado (ESC),
 * y adaptable para confirmaciones de advertencia o peligro.
 */
export default function ConfirmModal({
  isOpen,
  onClose,
  onConfirm,
  title = '¿Confirmar acción?',
  message = '¿Está seguro de que desea continuar?',
  confirmText = 'Confirmar',
  cancelText = 'Cancelar',
  variant = 'warning', // 'warning' | 'danger' | 'primary'
  icon = null,
}) {
  const modalRef = useRef(null);
  const titleId = useId();

  useEffect(() => {
    if (!isOpen) return;

    function handleKeyDown(e) {
      if (e.key === 'Escape') {
        onClose?.();
        return;
      }

      if (e.key === 'Tab' && modalRef.current) {
        const focusableElements = modalRef.current.querySelectorAll(
          'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
        );
        if (focusableElements.length === 0) return;

        const firstElement = focusableElements[0];
        const lastElement = focusableElements[focusableElements.length - 1];

        if (e.shiftKey) {
          if (document.activeElement === firstElement) {
            e.preventDefault();
            lastElement.focus();
          }
        } else {
          if (document.activeElement === lastElement) {
            e.preventDefault();
            firstElement.focus();
          }
        }
      }
    }

    const timer = setTimeout(() => {
      if (modalRef.current) {
        const focusable = modalRef.current.querySelector('button.btn:not(.btn-outline), button');
        if (focusable) focusable.focus();
      }
    }, 50);

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
      clearTimeout(timer);
    };
  }, [isOpen, onClose]);

  useEffect(() => {
    if (!isOpen) return undefined;
    return registerOpenModal();
  }, [isOpen]);

  if (!isOpen) return null;

  const isDanger = variant === 'danger';
  const isWarning = variant === 'warning';

  return (
    <div className="modal-overlay cfm-overlay" onClick={onClose}>
      <div
        ref={modalRef}
        className="modal-container card cfm-container"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
      >
        <div className="cfm-icon-row">
          {icon || (
            <div
              className="cfm-icon-circle"
              style={{
                backgroundColor: isDanger ? 'var(--danger-light)' : 'var(--warning-light)',
                color: isDanger ? 'var(--danger)' : 'var(--warning)',
              }}
            >
              <AlertTriangle size={28} />
            </div>
          )}
        </div>

        <h3
          id={titleId}
          className="cfm-title"
        >
          {title}
        </h3>

        <p className="cfm-message">
          {message}
        </p>

        <div className="d-flex gap-3 justify-center">
          <button
            type="button"
            className="btn btn-outline cfm-btn"
            onClick={onClose}
          >
            {cancelText}
          </button>
          <button
            type="button"
            className="btn cfm-btn cfm-btn-confirm"
            style={{
              backgroundColor: isDanger ? 'var(--danger)' : isWarning ? 'var(--warning)' : 'var(--primary-color)',
            }}
            onClick={onConfirm}
          >
            {confirmText}
          </button>
        </div>
      </div>
    </div>
  );
}
