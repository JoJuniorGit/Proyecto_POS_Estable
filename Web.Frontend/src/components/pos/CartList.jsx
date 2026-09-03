import { Plus, Minus, Trash2 } from 'lucide-react';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import QuantityInput from './QuantityInput';
import { getLineAmounts } from '../../utils/formatters';

export default function CartList({ items, selectedItemId, onSelectItem, onUpdateQty, onUpdateQuantity, onRemoveItem }) {
  const { exchangeRate } = useExchangeRate();
  const updateQty = onUpdateQty || onUpdateQuantity;

  return (
    <div className="cart-list-mobile">
      {items.map((item) => {
        const { unitBsS, subtotalBsS } = getLineAmounts(item, exchangeRate);

        const isSelected = selectedItemId === item.id;

        return (
          <div
            key={item.id}
            className={`cart-card-mobile ${isSelected ? 'selected' : ''}`}
            onClick={() => onSelectItem(item.id)}
          >
            <div className="cart-card-header">
              <span className="cart-card-title">{item.productName}</span>
              <button
                type="button"
                className="delete-btn"
                onClick={(e) => {
                  e.stopPropagation();
                  onRemoveItem(item.id);
                }}
              >
                <Trash2 size={16} />
              </button>
            </div>

            <div className="cart-card-body">
              <div className="cart-card-unit-price">
                Bs.S {unitBsS.toFixed(2)} c/u
              </div>

              <div className="cart-card-bottom">
                <div className="qty-controls" onClick={(e) => e.stopPropagation()}>
                  {(() => {
                    const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                    const isAtMin = item.quantity <= step;
                    return (
                      <button
                        type="button"
                        className="qty-btn"
                        disabled={isAtMin}
                        onClick={() => {
                          const newQty = Math.round((item.quantity - step) * 1000) / 1000;
                          if (newQty >= step) {
                            updateQty?.(item.id, newQty);
                          }
                        }}
                        title={isAtMin ? "Cantidad mínima" : "Disminuir cantidad"}
                      >
                        <Minus size={14} />
                      </button>
                    );
                  })()}
                  <QuantityInput item={item} onUpdateQty={updateQty} style={{
                    width: '52px',
                    textAlign: 'center',
                    border: '1px solid var(--border)',
                    borderRadius: '4px',
                    padding: '2px 4px',
                    fontSize: '0.85rem',
                    fontWeight: 'bold',
                    backgroundColor: 'var(--bg-input, var(--bg-card))',
                    color: 'var(--text-primary)'
                  }} />
                  <button
                    type="button"
                    className="qty-btn"
                    onClick={() => {
                      const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                      const newQty = Math.round((item.quantity + step) * 1000) / 1000;
                      updateQty?.(item.id, newQty);
                    }}
                  >
                    <Plus size={14} />
                  </button>
                </div>

                <div className="cart-card-subtotal">
                  Bs.S {subtotalBsS.toFixed(2)}
                </div>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}
