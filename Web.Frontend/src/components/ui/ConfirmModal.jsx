import { useEffect, useRef } from 'react';
import { AlertTriangle } from 'lucide-react';

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

  if (!isOpen) return null;

  const isDanger = variant === 'danger';
  const isWarning = variant === 'warning';

  return (
    <div className="modal-overlay" style={{ zIndex: 1200 }} onClick={onClose}>
      <div
        ref={modalRef}
        className="modal-container card"
        style={{
          maxWidth: '430px',
          width: '92%',
          padding: '1.75rem 1.5rem',
          textAlign: 'center',
          borderRadius: '12px',
          boxShadow: '0 20px 25px -5px rgba(0, 0, 0, 0.35), 0 10px 10px -5px rgba(0, 0, 0, 0.2)',
          border: '1px solid var(--border)',
          backgroundColor: 'var(--bg-surface)',
          color: 'var(--text-primary)',
          margin: 'auto',
        }}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
      >
        <div style={{ display: 'flex', justifyContent: 'center', marginBottom: '1.1rem' }}>
          {icon || (
            <div
              style={{
                width: '56px',
                height: '56px',
                borderRadius: '50%',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: isDanger ? 'rgba(239, 68, 68, 0.15)' : 'rgba(245, 158, 11, 0.15)',
                color: isDanger ? '#ef4444' : '#f59e0b',
              }}
            >
              <AlertTriangle size={28} />
            </div>
          )}
        </div>

        <h3
          id="confirm-dialog-title"
          style={{
            fontSize: '1.25rem',
            fontWeight: 700,
            marginBottom: '0.6rem',
            color: 'var(--text-primary)',
            textAlign: 'center',
          }}
        >
          {title}
        </h3>

        <p
          style={{
            fontSize: '0.95rem',
            lineHeight: '1.45',
            color: 'var(--text-secondary, var(--text-muted))',
            marginBottom: '1.75rem',
            padding: '0 0.5rem',
          }}
        >
          {message}
        </p>

        <div style={{ display: 'flex', gap: '0.75rem', justifyContent: 'center' }}>
          <button
            type="button"
            className="btn btn-outline"
            style={{
              flex: 1,
              padding: '0.75rem 1rem',
              fontWeight: 600,
              fontSize: '0.95rem',
              borderRadius: '8px',
              cursor: 'pointer',
            }}
            onClick={onClose}
          >
            {cancelText}
          </button>
          <button
            type="button"
            className="btn"
            style={{
              flex: 1,
              padding: '0.75rem 1rem',
              fontWeight: 600,
              fontSize: '0.95rem',
              borderRadius: '8px',
              backgroundColor: isDanger ? '#ef4444' : isWarning ? '#f59e0b' : 'var(--color-primary)',
              color: '#ffffff',
              border: 'none',
              cursor: 'pointer',
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
