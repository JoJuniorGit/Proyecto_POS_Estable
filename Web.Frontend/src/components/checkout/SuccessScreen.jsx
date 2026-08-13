import { useEffect } from 'react';
import { CheckCircle2, ShoppingBag, Clock, Check } from 'lucide-react';

export default function SuccessScreen({ 
  invoiceNumber, 
  title = "¡Venta Completada!",
  badgeText,
  message = "La transacción fue registrada exitosamente en el sistema.",
  buttonText = "Aceptar",
  type = "checkout",
  onClose 
}) {
  useEffect(() => {
    const timer = setTimeout(() => {
      onClose();
    }, 4500);
    return () => clearTimeout(timer);
  }, [onClose]);

  const displayBadge = badgeText || (invoiceNumber ? `Factura N° ${invoiceNumber.toString().padStart(6, '0')}` : null);

  return (
    <div className="success-overlay" onClick={onClose}>
      <div className="success-modal card text-center" onClick={(e) => e.stopPropagation()}>
        <div className="success-icon-wrapper">
          {type === 'hold' ? (
            <Clock size={72} className="success-icon animate-bounce-short" style={{ color: 'var(--accent-primary, #6366f1)' }} />
          ) : (
            <CheckCircle2 size={72} className="success-icon animate-bounce-short" style={{ color: 'var(--success, #10b981)' }} />
          )}
        </div>
        <h2 className="success-title">{title}</h2>
        {displayBadge && (
          <div className="success-invoice-badge">
            {displayBadge}
          </div>
        )}
        <p className="success-text mt-2">
          {message}
        </p>

        <button
          type="button"
          className="btn btn-primary btn-block"
          style={{ marginTop: '1.5rem', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '8px' }}
          onClick={onClose}
        >
          {type === 'checkout' ? <ShoppingBag size={18} /> : <Check size={18} />}
          {buttonText}
        </button>
      </div>
    </div>
  );
}
