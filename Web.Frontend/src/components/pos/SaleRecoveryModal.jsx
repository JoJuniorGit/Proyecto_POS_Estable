import { formatUSD } from '../../utils/formatters';
import './SaleRecoveryModal.css';

export default function SaleRecoveryModal({ isOpen, recovery, isProcessing = false, error = null, onRecover, onDiscard }) {
  if (!isOpen) return null;

  const customerText = recovery?.customerName ? ` de ${recovery.customerName}` : '';
  const itemCount = recovery?.itemCount ?? 0;
  const totalUSD = recovery?.totalUSD || 0;

  return (
    <div className="modal-overlay srm-overlay">
      <div
        className="modal-container card srm-container"
        role="dialog"
        aria-modal="true"
        aria-labelledby="sale-recovery-title"
        aria-describedby="sale-recovery-detail"
      >
        <h3 id="sale-recovery-title" className="srm-title">Venta sin finalizar detectada</h3>

        <p id="sale-recovery-detail" className="srm-detail">
          Se encontró una venta en curso sin finalizar{customerText} con {itemCount} artículo(s) por un total de {formatUSD(totalUSD)}. ¿Desea recuperarla donde la dejó?
        </p>

        {error && (
          <div className="alert alert-danger srm-alert">{error}</div>
        )}

        <div className="srm-actions">
          <button
            type="button"
            className="btn btn-outline"
            onClick={onDiscard}
          >
            Descartar
          </button>

          <button
            type="button"
            className="btn btn-primary"
            disabled={isProcessing}
            onClick={onRecover}
          >
            {isProcessing ? 'Recuperando...' : 'Recuperar venta'}
          </button>
        </div>
      </div>
    </div>
  );
}
