import { Trash2, CreditCard, Loader2, Clock } from 'lucide-react';
import { useCart } from '../../context/CartContext';
import { formatBsS, formatUSD } from '../../utils/formatters';

export default function SummaryPanel({ onCheckout, onHold }) {
  const {
    currentSale,
    items,
    selectedItemId,
    subtotalBsS,
    totalBsS,
    totalUSD,
    removeItem,
    changePriceList,
    loading,
  } = useCart();

  const isEmpty = items.length === 0;
  const priceListType = currentSale?.priceListType || 'Retail';

  const handlePriceListChange = async (newType) => {
    if (newType === priceListType) return;
    try {
      await changePriceList(newType);
    } catch (err) {
      alert(err.message || 'No se pudo cambiar la lista de precios.');
    }
  };

  return (
    <div className="summary-panel card">
      <div className="summary-header">
        <h3 className="summary-title">Resumen de Venta</h3>
        {selectedItemId && (
          <button
            type="button"
            className="btn btn-outline btn-sm btn-danger-text"
            onClick={() => removeItem(selectedItemId)}
            title="Eliminar producto seleccionado"
          >
            <Trash2 size={14} /> Eliminar Seleccionado
          </button>
        )}
      </div>

      <div className="summary-details">
        <div className="summary-row" style={{ alignItems: 'center', marginBottom: '0.75rem' }}>
          <span className="summary-label">Lista de Precios</span>
          <div style={{ display: 'flex', gap: '6px' }}>
            <button
              type="button"
              className={`btn btn-sm ${priceListType === 'Retail' ? 'btn-pricelist-active' : 'btn-pricelist-inactive'}`}
              onClick={() => handlePriceListChange('Retail')}
              disabled={loading || currentSale?.status === 'Completed'}
            >
              Detal
            </button>
            <button
              type="button"
              className={`btn btn-sm ${priceListType === 'Wholesale' ? 'btn-pricelist-active' : 'btn-pricelist-inactive'}`}
              onClick={() => handlePriceListChange('Wholesale')}
              disabled={loading || currentSale?.status === 'Completed'}
            >
              Mayor
            </button>
          </div>
        </div>

        <div className="summary-row">
          <span className="summary-label">Subtotal</span>
          <span className="summary-val">{formatBsS(subtotalBsS || 0)}</span>
        </div>

        <div className="summary-divider" />

        <div className="summary-total-container">
          <div className="summary-total-label">TOTAL</div>
          <div className="summary-total-primary">
            {formatBsS(totalBsS || 0)}
          </div>
          <div className="summary-total-secondary">
            {formatUSD(totalUSD || 0)}
          </div>
        </div>
      </div>

      <div className="summary-actions">
        <button
          type="button"
          className="btn btn-primary btn-lg btn-block checkout-btn"
          disabled={isEmpty || loading}
          onClick={onCheckout}
        >
          {loading ? (
            <>
              <Loader2 className="animate-spin" size={20} />
              Procesando...
            </>
          ) : (
            <>
              <CreditCard size={20} />
              COBRAR (F1)
            </>
          )}
        </button>

        <button
          type="button"
          className="btn btn-block btn-hold-action"
          disabled={isEmpty || loading}
          onClick={onHold}
        >
          <Clock size={18} />
          GUARDAR EN ESPERA (F4)
        </button>
      </div>
    </div>
  );
}
