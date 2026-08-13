import { Trash2 } from 'lucide-react';
import { formatBsS, formatUSD } from '../../utils/formatters';

export default function PaymentList({ payments, onRemovePayment }) {
  if (!payments || payments.length === 0) {
    return (
      <div className="payment-list-empty">
        No se han agregado pagos.
      </div>
    );
  }

  return (
    <div className="payment-list">
      <h4 className="payment-list-title">Pagos Aplicados</h4>
      {payments.map((p, idx) => (
        <div key={idx} className="payment-item">
          <div className="payment-item-main">
            <span className="payment-item-name">{p.methodName}</span>
            {p.reference && (
              <span className="payment-item-ref">Ref: {p.reference}</span>
            )}
          </div>

          <div className="payment-item-amounts">
            <span className="payment-item-bss">{formatBsS(p.amountBsS)}</span>
            <span className="payment-item-usd font-muted">({formatUSD(p.amountUsd)})</span>
            <button
              type="button"
              className="delete-btn"
              onClick={() => onRemovePayment(idx)}
              title="Eliminar este pago"
            >
              <Trash2 size={16} />
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}
