import React, { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import SearchBar from './SearchBar';
import QuantityInput from './QuantityInput';
import { updateSaleItems } from '../../services/salesApi';
import { formatBsS, formatUSD } from '../../utils/formatters';
import { Trash2, AlertTriangle, Save, Loader2, Plus, Minus } from 'lucide-react';

export default function EditSaleModal({ isOpen, onClose, sale, exchangeRate, onSuccess }) {
  const [items, setItems] = useState([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState(null);

  useEffect(() => {
    if (sale?.items) {
      setItems(sale.items.map(i => ({
        productId: i.productId,
        productName: i.productName,
        displayProductName: i.displayProductName || (i.unitOfMeasure && i.unitOfMeasure !== 'Und' ? `${i.productName} (${i.unitOfMeasure})` : i.productName),
        isFractional: i.isFractional,
        unitOfMeasure: i.unitOfMeasure,
        quantity: i.quantity,
        unitPrice: i.unitPrice || 0,
        subtotal: (i.quantity || 1) * (i.unitPrice || 0)
      })));
    } else {
      setItems([]);
    }
    setError(null);
  }, [sale]);

  const handleAddProduct = (prod) => {
    if (!prod) return;
    const price = prod.priceUSD || prod.unitPriceUSD || 0;
    setItems(prev => {
      const existingIdx = prev.findIndex(i => i.productId === prod.id);
      if (existingIdx >= 0) {
        const updated = [...prev];
        const step = !updated[existingIdx].isFractional ? 1 : (updated[existingIdx].unitOfMeasure === 'Grs' || updated[existingIdx].unitOfMeasure === 'Ml' ? 100 : updated[existingIdx].unitOfMeasure === 'Lb' ? 0.25 : 0.100);
        const newQty = Math.round((updated[existingIdx].quantity + step) * 1000) / 1000;
        updated[existingIdx] = {
          ...updated[existingIdx],
          quantity: newQty,
          subtotal: newQty * updated[existingIdx].unitPrice
        };
        return updated;
      } else {
        return [...prev, {
          productId: prod.id,
          productName: prod.name,
          displayProductName: prod.displayProductName || (prod.unitOfMeasure && prod.unitOfMeasure !== 'Und' ? `${prod.name} (${prod.unitOfMeasure})` : prod.name),
          isFractional: prod.isFractional,
          unitOfMeasure: prod.unitOfMeasure,
          quantity: 1,
          unitPrice: price,
          subtotal: price
        }];
      }
    });
  };

  const handleUpdateQuantity = (idx, newQty) => {
    if (newQty === '') {
      setItems(prev => {
        const updated = [...prev];
        updated[idx] = { ...updated[idx], quantity: '' };
        return updated;
      });
      return;
    }
    const isFrac = items[idx]?.isFractional;
    const rawNum = isFrac ? parseFloat(newQty) : parseInt(newQty, 10);
    const qty = isNaN(rawNum) ? (isFrac ? 0.001 : 1) : rawNum;
    const roundedQty = isFrac ? Math.round(qty * 1000) / 1000 : qty;
    setItems(prev => {
      const updated = [...prev];
      updated[idx] = {
        ...updated[idx],
        quantity: roundedQty,
        subtotal: roundedQty * updated[idx].unitPrice
      };
      return updated;
    });
  };

  const handleRemoveItem = (idx) => {
    setItems(prev => prev.filter((_, i) => i !== idx));
  };

  // Cálculos financieros
  const totalPaidUSD = sale?.totalPaidUSD || (sale?.payments?.reduce((acc, p) => acc + (p.amount || 0), 0)) || 0;
  const newTotalUSD = items.reduce((acc, i) => acc + (i.quantity * i.unitPrice), 0);
  const rateToUse = sale?.appliedRate || exchangeRate || 1;
  const newTotalBsS = newTotalUSD * rateToUse;
  const newRemainingBalanceUSD = Math.max(0, newTotalUSD - totalPaidUSD);

  // Validaciones
  const isBelowPaidAmount = newTotalUSD < (totalPaidUSD - 0.01);
  const canSave = items.length > 0 && !isBelowPaidAmount;

  const handleSave = async () => {
    if (!sale?.id || !canSave) return;
    setIsSaving(true);
    setError(null);
    try {
      const payloadItems = items.map(i => ({
        productId: i.productId,
        quantity: i.quantity,
        unitPrice: i.unitPrice
      }));
      await updateSaleItems(sale.id, payloadItems);
      if (onSuccess) onSuccess();
      onClose();
    } catch (err) {
      console.error('[EditSaleModal] Error al guardar cambios:', err);
      setError(err.response?.data || err.message || 'Error al guardar las modificaciones del pedido.');
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={`✏️ Editar Productos del Pedido #${sale?.id || ''}`} maxWidth="680px" overflowVisible={true}>
      {error && (
        <div className="alert alert-danger mb-3 text-sm">
          {error}
        </div>
      )}

      {/* Buscador de Productos (Mismo componente del POS) */}
      <div className="mb-3" style={{ position: 'relative', zIndex: 1000 }}>
        <label className="font-medium text-sm mb-1 d-block text-muted">Agregar producto al pedido:</label>
        <SearchBar onSelectProduct={handleAddProduct} />
      </div>

      {/* Tabla de Productos Editables con Estilo del Carrito del POS */}
      <div className="cart-table-wrapper custom-scrollbar mb-4" style={{ maxHeight: '250px', overflowY: 'auto' }}>
        {items.length === 0 ? (
          <div className="text-center py-4 text-muted border-dashed" style={{ borderRadius: '8px' }}>
            No hay productos en la lista.
          </div>
        ) : (
          <table className="cart-table">
            <thead>
              <tr>
                <th>Producto</th>
                <th className="text-center">Cant.</th>
                <th className="text-right">Precio Bs.S</th>
                <th className="text-right">Subtotal Bs.S</th>
                <th className="text-center" style={{ width: '50px' }}>Acción</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, idx) => {
                const unitBsS = item.unitPrice * rateToUse;
                const subtotalBsS = item.subtotal * rateToUse;
                const step = !item.isFractional ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);

                return (
                  <tr key={idx} className="cart-row">
                    <td className="font-medium">{item.displayProductName || item.productName}</td>

                    <td className="text-center">
                      <div className="qty-controls">
                        <button
                          type="button"
                          className="qty-btn"
                          onClick={() => {
                            const newQty = Math.round((item.quantity - step) * 1000) / 1000;
                            if (newQty > 0) handleUpdateQuantity(idx, newQty);
                            else handleRemoveItem(idx);
                          }}
                          title="Disminuir cantidad"
                        >
                          <Minus size={14} />
                        </button>
                        <QuantityInput
                          item={item}
                          onUpdateQty={(_, newQty) => handleUpdateQuantity(idx, newQty)}
                        />
                        <button
                          type="button"
                          className="qty-btn"
                          onClick={() => {
                            const newQty = Math.round((item.quantity + step) * 1000) / 1000;
                            handleUpdateQuantity(idx, newQty);
                          }}
                          title="Aumentar cantidad"
                        >
                          <Plus size={14} />
                        </button>
                      </div>
                    </td>

                    <td className="text-right">{formatBsS(unitBsS)}</td>

                    <td className="text-right font-bold color-primary">
                      {formatBsS(subtotalBsS)}
                    </td>

                    <td className="text-center">
                      <button
                        type="button"
                        className="delete-btn"
                        onClick={() => handleRemoveItem(idx)}
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
        )}
      </div>

      {/* Alertas de Validación */}
      {isBelowPaidAmount && (
        <div className="alert alert-warning mb-3 flex-align-center gap-2" style={{ fontSize: '0.85rem' }}>
          <AlertTriangle size={18} className="flex-shrink-0" />
          <span>El nuevo total ({formatUSD(newTotalUSD)}) no puede ser menor a lo ya abonado por el cliente ({formatUSD(totalPaidUSD)}).</span>
        </div>
      )}


      {/* Resumen Financiero */}
      <div 
        className="border text-sm mb-4"
        style={{ 
          backgroundColor: 'var(--bg-secondary, rgba(255,255,255,0.03))', 
          borderRadius: '8px', 
          padding: '12px 16px'
        }}
      >
        <div className="flex-between mb-1">
          <span className="text-muted">Nuevo Total USD / Bs.S:</span>
          <span className="font-bold">{formatUSD(newTotalUSD)} <span className="color-primary">({formatBsS(newTotalBsS)})</span></span>
        </div>
        <div className="flex-between mb-1">
          <span className="text-muted">Total Ya Abonado:</span>
          <span className="font-bold text-success">{formatUSD(totalPaidUSD)}</span>
        </div>
        <div className="flex-between mb-1">
          <span className="text-muted">Nuevo Saldo Restante:</span>
          <span className="font-bold text-danger">{formatUSD(newRemainingBalanceUSD)}</span>
        </div>
      </div>

      {/* Acciones */}
      <div className="d-flex justify-center flex-align-center gap-3 flex-wrap">
        <button type="button" className="btn btn-outline" onClick={onClose} disabled={isSaving}>
          Cancelar
        </button>
        <button
          type="button"
          className="btn btn-primary"
          onClick={handleSave}
          disabled={!canSave || isSaving}
        >
          {isSaving ? (
            <>
              <Loader2 size={16} className="animate-spin" /> Guardando...
            </>
          ) : (
            <>
              <Save size={16} /> Guardar Cambios
            </>
          )}
        </button>
      </div>
    </Modal>
  );
}
