import { Plus, Minus, Trash2 } from 'lucide-react';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import QuantityInput from './QuantityInput';

export default function CartList({ items, selectedItemId, onSelectItem, onUpdateQty, onRemoveItem }) {
  const { exchangeRate } = useExchangeRate();

  return (
    <div className="cart-list-mobile">
      {items.map((item) => {
        const unitBsS = item.unitPrice > 0
          ? item.unitPrice * exchangeRate
          : (item.unitPriceBsS || 0);
        const subtotalBsS = item.unitPrice > 0
          ? item.subtotal * exchangeRate
          : (item.subtotalBsS || 0);

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
                  <button
                    type="button"
                    className="qty-btn"
                    onClick={() => {
                      const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                      const newQty = Math.round((item.quantity - step) * 1000) / 1000;
                      if (newQty > 0) onUpdateQty(item.id, newQty);
                      else if (onRemoveItem) onRemoveItem(item.id);
                    }}
                  >
                    <Minus size={14} />
                  </button>
                  <QuantityInput item={item} onUpdateQty={onUpdateQty} style={{
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
                      onUpdateQty(item.id, newQty);
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
