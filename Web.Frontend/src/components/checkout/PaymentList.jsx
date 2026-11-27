import { Trash2 } from 'lucide-react';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { MONETARY_MODE_DUAL } from '../../utils/monetaryProfile';

export default function PaymentList({ payments, onRemovePayment }) {
  // 8.211-T4b2b1 (S9): en Dual se muestran las dos monedas; en Single el monto local ya es la
  // primaria y la segunda línea sería un duplicado con otro símbolo.
  const { mode, formatPrimary, formatSecondary } = useMonetaryProfile();
  const isDual = mode === MONETARY_MODE_DUAL;

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
      {payments.map((p) => (
        // 8.5-WEB5: key estable por uid generado al crear el pago; remover por objeto evita
        // bugs de React key={index} al eliminar o reordenar pagos rápidos.
        <div key={p.uid} className="payment-item">
          <div className="payment-item-main">
            <span className="payment-item-name">{p.methodName}</span>
            {p.reference && (
              <span className="payment-item-ref">Ref: {p.reference}</span>
            )}
          </div>

          <div className="payment-item-amounts">
            <span className="payment-item-bss">{formatSecondary(p.amountBsS)}</span>
            {isDual && (
              <span className="payment-item-usd font-muted">({formatPrimary(p.amountUsd)})</span>
            )}
            <button
              type="button"
              className="delete-btn"
              onClick={() => onRemovePayment(p)}
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
