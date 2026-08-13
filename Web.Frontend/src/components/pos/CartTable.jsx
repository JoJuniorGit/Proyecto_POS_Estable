import { Plus, Minus, Trash2 } from 'lucide-react';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import QuantityInput from './QuantityInput';
import { useCart } from '../../context/CartContext';
import { formatBsS } from '../../utils/formatters';

export default function CartTable({ items, selectedItemId, onSelectItem, onUpdateQty, onRemoveItem }) {
  const { exchangeRate } = useExchangeRate();
  const { currentSale } = useCart();
  const isWholesaleMode = (currentSale?.priceListType || '').toLowerCase() === 'wholesale';

  return (
    <div className="cart-table-wrapper">
      <table className="cart-table">
        <thead>
          <tr>
            <th>Producto</th>
            <th className="text-center" style={{ textAlign: 'center', minWidth: '130px' }}>Cant.</th>
            <th className="text-right" style={{ textAlign: 'right', paddingRight: '1rem' }}>Precio Bs.S</th>
            <th className="text-right" style={{ textAlign: 'right', paddingRight: '1rem' }}>Subtotal Bs.S</th>
            <th className="text-center" style={{ width: '60px', textAlign: 'center' }}>Acción</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item) => {
            const unitBsS = item.unitPrice > 0
              ? item.unitPrice * exchangeRate
              : (item.unitPriceBsS || 0);
            const subtotalBsS = item.unitPrice > 0
              ? item.subtotal * exchangeRate
              : (item.subtotalBsS || 0);

            const isSelected = selectedItemId === item.id;
            const isWholesaleApplied = isWholesaleMode && item.quantity >= 6;

            return (
              <tr
                key={item.id}
                className={`cart-row ${isSelected ? 'selected' : ''}`}
                onClick={() => onSelectItem(item.id)}
              >
                <td className="font-medium">
                  {item.displayProductName || (item.unitOfMeasure && item.unitOfMeasure !== 'Und' ? `${item.productName} (${item.unitOfMeasure})` : item.productName)}
                  {isWholesaleMode && (
                    isWholesaleApplied ? (
                      <span style={{ marginLeft: '8px', fontSize: '0.72rem', backgroundColor: '#10b981', color: '#ffffff', padding: '2px 6px', borderRadius: '4px', fontWeight: 'bold' }}>
                        Mayorista
                      </span>
                    ) : (
                      <span style={{ marginLeft: '8px', fontSize: '0.72rem', backgroundColor: '#f59e0b', color: '#ffffff', padding: '2px 6px', borderRadius: '4px', fontWeight: '500' }}>
                        Detal
                      </span>
                    )
                  )}
                </td>

                <td className="text-center" style={{ textAlign: 'center', verticalAlign: 'middle' }}>
                  <div className="qty-controls" style={{ display: 'inline-flex', margin: '0 auto' }} onClick={(e) => e.stopPropagation()}>
                    <button
                      type="button"
                      className="qty-btn"
                      onClick={() => {
                        const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                        const newQty = Math.round((item.quantity - step) * 1000) / 1000;
                        if (newQty > 0) onUpdateQty(item.id, newQty);
                        else if (onRemoveItem) onRemoveItem(item.id);
                      }}
                      title="Disminuir cantidad"
                    >
                      <Minus size={14} />
                    </button>
                    <QuantityInput item={item} onUpdateQty={onUpdateQty} />
                    <button
                      type="button"
                      className="qty-btn"
                      onClick={() => {
                        const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                        const newQty = Math.round((item.quantity + step) * 1000) / 1000;
                        onUpdateQty(item.id, newQty);
                      }}
                      title="Aumentar cantidad"
                    >
                      <Plus size={14} />
                    </button>
                  </div>
                </td>

                <td className="text-right font-medium" style={{ textAlign: 'right', paddingRight: '1rem', whiteSpace: 'nowrap' }}>
                  {formatBsS(unitBsS)}
                </td>

                <td className="text-right font-bold color-primary" style={{ textAlign: 'right', paddingRight: '1rem', whiteSpace: 'nowrap' }}>
                  {formatBsS(subtotalBsS)}
                </td>

                <td className="text-center" style={{ textAlign: 'center', verticalAlign: 'middle' }} onClick={(e) => e.stopPropagation()}>
                  <button
                    type="button"
                    className="delete-btn"
                    onClick={() => onRemoveItem(item.id)}
                    title="Eliminar producto"
                  >
                    <Trash2 size={16} />
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
