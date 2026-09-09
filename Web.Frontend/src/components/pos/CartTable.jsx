import { Plus, Minus, Trash2 } from 'lucide-react';
import { useExchangeRate } from '../../context/ExchangeRateContext';
import QuantityInput from './QuantityInput';
import { useCart } from '../../context/CartContext';
import { formatBsS, getLineAmounts } from '../../utils/formatters';
import './CartTable.css';

export default function CartTable({ items, selectedItemId, onSelectItem, onUpdateQty, onUpdateQuantity, onRemoveItem }) {
  const { exchangeRate } = useExchangeRate();
  const { currentSale } = useCart();
  const isWholesaleMode = (currentSale?.priceListType || '').toLowerCase() === 'wholesale';
  const updateQty = onUpdateQty || onUpdateQuantity;

  return (
    <div className="cart-table-wrapper">
      <table className="cart-table">
        <thead>
          <tr>
            <th>Producto</th>
            <th className="text-center ct-qty-col">Cant.</th>
            <th className="text-right ct-price-col">Precio Bs.S</th>
            <th className="text-right ct-price-col">Subtotal Bs.S</th>
            <th className="text-center ct-action-col">Acción</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item) => {
            const { unitBsS, subtotalBsS } = getLineAmounts(item, currentSale?.appliedRate || exchangeRate);

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
                      <span className="ct-badge ct-badge-wholesale">
                        Mayorista
                      </span>
                    ) : (
                      <span className="ct-badge ct-badge-retail">
                        Detal
                      </span>
                    )
                  )}
                </td>

                <td className="text-center ct-valign-middle">
                  <div className="qty-controls ct-qty-center" onClick={(e) => e.stopPropagation()}>
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
                          title={isAtMin ? "Cantidad mínima (use el icono de eliminar para quitar del carrito)" : "Disminuir cantidad"}
                        >
                          <Minus size={14} />
                        </button>
                      );
                    })()}
                    <QuantityInput item={item} isFractional={item.isFractional} onUpdateQty={updateQty} />
                    <button
                      type="button"
                      className="qty-btn"
                      onClick={() => {
                        const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
                        const newQty = Math.round((item.quantity + step) * 1000) / 1000;
                        updateQty?.(item.id, newQty);
                      }}
                      title="Aumentar cantidad"
                    >
                      <Plus size={14} />
                    </button>
                  </div>
                </td>

                <td className="text-right font-medium text-nowrap ct-price-col">
                  {formatBsS(unitBsS)}
                </td>

                <td className="text-right font-bold color-primary text-nowrap ct-price-col">
                  {formatBsS(subtotalBsS)}
                </td>

                <td className="text-center ct-valign-middle" onClick={(e) => e.stopPropagation()}>
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
