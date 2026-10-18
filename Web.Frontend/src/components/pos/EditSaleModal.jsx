import React, { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import SearchBar from './SearchBar';
import QuantityInput from './QuantityInput';
import { updateSaleItems } from '../../services/salesApi';
import { formatBsS, formatUSD, formatNumberEs } from '../../utils/formatters';
import { Trash2, AlertTriangle, Save, Loader2, Plus, Minus } from 'lucide-react';
import './EditSaleModal.css';

const toBsSCeiling = (usd, rate) => (usd > 0 && rate > 0 ? Math.ceil(usd * rate * 100) / 100 : 0);

/**
 * 8.186-T7b (S2 ext. cuentas abiertas): tasa anclada de la cuenta — la MISMA que el server
 * persiste al editar los ítems (`sale.AppliedRate`) y la que usa el POS local. La tasa vigente
 * (SignalR) puede haber cambiado después de abrir la cuenta; usarla aquí desfasaría el display
 * de lo que se persiste. Fallback a la tasa vigente solo si la venta no trae una anclada.
 */
export function resolveAccountRate(sale, exchangeRate) {
  const appliedRate = Number(sale?.appliedRate);
  return appliedRate > 0 ? appliedRate : exchangeRate;
}

/**
 * Bs.S unitario de una línea: snapshot persistido si existe; si no, techo canónico (2dp)
 * USD × tasa — la misma matemática de display que aplica el server.
 */
export function resolveUnitPriceBsS(unitPriceBsS, unitPriceUSD, rate) {
  const stored = Number(unitPriceBsS);
  if (stored > 0) return stored;
  return toBsSCeiling(Number(unitPriceUSD) || 0, rate);
}

/**
 * Resumen financiero reactivo del modal de edición. Toda la matemática de la cuenta ancla a su
 * tasa (`accountRate`): fallback Bs.S de ítems sin snapshot, abonos sin `amountBsS` e inversas
 * de display. "Total Ya Abonado" consume el Bs.S REAL ya registrado (`totalPaidBsS`), nunca una
 * síntesis USD × tasa vigente: tras un cambio de tasa las tres filas reconcilian
 * (`newTotalBsS == totalPaidBsS + newRemainingBalanceBsS`, con clamp del restante en 0).
 */
export function computeEditSaleSummary({ items = [], sale = null, exchangeRate } = {}) {
  const accountRate = resolveAccountRate(sale, exchangeRate);

  const totalPaidUSD = Number(sale?.totalPaidUSD || (sale?.payments?.reduce((acc, p) => acc + (Number(p.amount) || 0), 0)) || 0);
  const newTotalUSD = items.reduce((acc, i) => acc + ((Number(i.quantity) || 0) * (Number(i.unitPrice) || 0)), 0);
  const newTotalBsS = Math.round(items.reduce((acc, i) => {
    const unitBsS = resolveUnitPriceBsS(i.unitPriceBsS, i.unitPrice, accountRate);
    return acc + ((Number(i.quantity) || 0) * unitBsS);
  }, 0) * 100) / 100;
  const newRemainingBalanceUSD = Math.max(0, newTotalUSD - totalPaidUSD);

  // ANEXO XV: paridad del monto redondeado en cuentas abiertas — el restante Bs.S es la deuda
  // efectiva (nuevo total por linea ya redondeado menos abonos Bs.S) y su $ es el inverso.
  const totalPaidBsS = (sale?.payments || []).reduce((acc, p) => acc + (Number(p.amountBsS) > 0 ? Number(p.amountBsS) : (Number(p.amount) || 0) * (Number(p.exchangeRate) || accountRate)), 0);
  const newRemainingBalanceBsS = Math.max(0, newTotalBsS - totalPaidBsS);
  const newTotalEffectiveUsd = accountRate > 0 && newTotalBsS > 0 ? Math.round((newTotalBsS / accountRate) * 100) / 100 : newTotalUSD;
  const newRemainingEffectiveUsd = accountRate > 0 ? Math.round((newRemainingBalanceBsS / accountRate) * 100) / 100 : newRemainingBalanceUSD;

  const isBelowPaidAmount = newTotalUSD < (totalPaidUSD - 0.01);

  return {
    accountRate,
    totalPaidUSD,
    newTotalUSD,
    newTotalBsS,
    totalPaidBsS,
    newRemainingBalanceBsS,
    newTotalEffectiveUsd,
    newRemainingEffectiveUsd,
    isBelowPaidAmount,
  };
}

export default function EditSaleModal({ isOpen, onClose, sale, exchangeRate, onSuccess }) {
  const [items, setItems] = useState([]);
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState(null);

  const accountRate = resolveAccountRate(sale, exchangeRate);

  useEffect(() => {
    if (sale?.items) {
      setItems(sale.items.map(i => {
        const unitPriceUSD = Number(i.unitPrice ?? i.unitPriceUSD ?? 0);
        const qty = Number(i.quantity) || 1;
        const unitPriceBsS = resolveUnitPriceBsS(i.unitPriceBsS, unitPriceUSD, accountRate);
        const isFractional = Boolean(
          i.isFractional ||
          i.isFractionable ||
          (i.unitOfMeasure && i.unitOfMeasure !== 'Und' && i.unitOfMeasure !== 0)
        );

        return {
          productId: i.productId,
          productName: i.productName,
          displayProductName: i.displayProductName || (i.unitOfMeasure && i.unitOfMeasure !== 'Und' ? `${i.productName} (${i.unitOfMeasure})` : i.productName),
          isFractional,
          unitOfMeasure: i.unitOfMeasure || 'Und',
          quantity: qty,
          unitPrice: unitPriceUSD,
          unitPriceBsS: unitPriceBsS,
          subtotal: Math.round(qty * unitPriceUSD * 100) / 100,
          subtotalBsS: Math.round(qty * unitPriceBsS * 100) / 100
        };
      }));
    } else {
      setItems([]);
    }
    setError(null);
  }, [sale, exchangeRate, accountRate]);

  const handleAddProduct = (prod) => {
    if (!prod) return;
    const price = Number(prod.priceUSD || prod.unitPriceUSD || prod.priceRetailUSD || 0);
    const isFrac = Boolean(
      prod.isFractional ||
      prod.isFractionable ||
      (prod.unitOfMeasure && prod.unitOfMeasure !== 'Und' && prod.unitOfMeasure !== 0)
    );
    const unitOfMeasure = prod.unitOfMeasure || 'Und';
    const unitBsS = toBsSCeiling(price, accountRate);

    setItems(prev => {
      const existingIdx = prev.findIndex(i => i.productId === prod.id);
      if (existingIdx >= 0) {
        const updated = [...prev];
        const current = updated[existingIdx];
        const step = !current.isFractional ? 1 : (current.unitOfMeasure === 'Grs' || current.unitOfMeasure === 'Ml' ? 100 : current.unitOfMeasure === 'Lb' ? 0.25 : 0.100);
        const newQty = Math.round(((Number(current.quantity) || 0) + step) * 1000) / 1000;
        const currentUnitBsS = resolveUnitPriceBsS(current.unitPriceBsS, current.unitPrice, accountRate);

        updated[existingIdx] = {
          ...current,
          quantity: newQty,
          unitPriceBsS: currentUnitBsS,
          subtotal: Math.round(newQty * current.unitPrice * 100) / 100,
          subtotalBsS: Math.round(newQty * currentUnitBsS * 100) / 100
        };
        return updated;
      } else {
        const initialQty = isFrac && (unitOfMeasure === 'Grs' || unitOfMeasure === 'Ml') ? 100 : (isFrac && unitOfMeasure === 'Lb' ? 0.25 : (isFrac ? 0.100 : 1));
        return [...prev, {
          productId: prod.id,
          productName: prod.name,
          displayProductName: prod.displayProductName || (unitOfMeasure !== 'Und' ? `${prod.name} (${unitOfMeasure})` : prod.name),
          isFractional: isFrac,
          unitOfMeasure: unitOfMeasure,
          quantity: initialQty,
          unitPrice: price,
          unitPriceBsS: unitBsS,
          subtotal: Math.round(initialQty * price * 100) / 100,
          subtotalBsS: Math.round(initialQty * unitBsS * 100) / 100
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
    const rawNum = typeof newQty === 'number' ? newQty : parseFloat(String(newQty).replace(',', '.'));
    const targetItem = items[idx];
    const isFrac = Boolean(
      targetItem?.isFractional ||
      targetItem?.isFractionable ||
      (targetItem?.unitOfMeasure && targetItem.unitOfMeasure !== 'Und' && targetItem.unitOfMeasure !== 0)
    );
    const qty = isNaN(rawNum) ? 1 : rawNum;
    const validatedQty = isFrac ? Math.round(qty * 1000) / 1000 : Math.max(1, Math.trunc(qty));
    setItems(prev => {
      const updated = [...prev];
      const current = updated[idx];
      const unitBsS = resolveUnitPriceBsS(current.unitPriceBsS, current.unitPrice, accountRate);
      const subUSD = Math.round(validatedQty * (current.unitPrice || 0) * 100) / 100;
      const subBsS = Math.round(validatedQty * unitBsS * 100) / 100;

      updated[idx] = {
        ...current,
        quantity: validatedQty,
        unitPriceBsS: unitBsS,
        subtotal: subUSD,
        subtotalBsS: subBsS
      };
      return updated;
    });
  };

  const handleRemoveItem = (idx) => {
    setItems(prev => prev.filter((_, i) => i !== idx));
  };

  // Cálculos financieros reactivos en tiempo real (8.186-T7b: resumen puro y testeable)
  const {
    totalPaidUSD,
    newTotalUSD,
    newTotalBsS,
    totalPaidBsS,
    newRemainingBalanceBsS,
    newTotalEffectiveUsd,
    newRemainingEffectiveUsd,
    isBelowPaidAmount,
  } = computeEditSaleSummary({ items, sale, exchangeRate });

  // Validaciones
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
      setError(err.message || 'Error al guardar las modificaciones del pedido.');
    } finally {
      setIsSaving(false);
    }
  };

  const handleClose = () => {
    if (!isSaving) {
      onClose?.();
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title={`✏️ Editar Productos del Pedido #${sale?.id || ''}`} maxWidth="680px" overflowVisible={true}>
      {error && (
        <div className="alert alert-danger mb-3 text-sm">
          {error}
        </div>
      )}

      {/* Buscador de Productos (Mismo componente del POS) */}
      <div className="mb-3 es-search-area">
        <label className="font-medium text-sm mb-1 d-block text-muted">Agregar producto al pedido:</label>
        <SearchBar onSelectProduct={handleAddProduct} />
      </div>

      {/* Tabla de Productos Editables con Estilo del Carrito del POS */}
      <div className="cart-table-wrapper custom-scrollbar mb-4 es-table-wrapper">
        {items.length === 0 ? (
          <div className="text-center py-4 text-muted border-dashed es-empty-table">
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
                <th className="text-center es-th-action">Acción</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item, idx) => {
                const qty = Number(item.quantity) || 0;
                const unitBsS = resolveUnitPriceBsS(item.unitPriceBsS, item.unitPrice, accountRate);
                const subtotalBsS = Math.round(qty * unitBsS * 100) / 100;
                const isFrac = Boolean(
                  item.isFractional ||
                  item.isFractionable ||
                  (item.unitOfMeasure && item.unitOfMeasure !== 'Und' && item.unitOfMeasure !== 0)
                );
                const step = !isFrac ? 1 : (item.unitOfMeasure === 'Grs' || item.unitOfMeasure === 'Ml' ? 100 : item.unitOfMeasure === 'Lb' ? 0.25 : 0.100);

                return (
                  <tr key={item.productId != null ? item.productId : item.productName} className="cart-row">
                    <td className="font-medium">{item.displayProductName || item.productName}</td>

                    <td className="text-center">
                      <div className="qty-controls">
                        <button
                          type="button"
                          className="qty-btn"
                          disabled={item.quantity <= step}
                          onClick={() => {
                            const newQty = Math.max(step, Math.round(((Number(item.quantity) || 0) - step) * 1000) / 1000);
                            handleUpdateQuantity(idx, newQty);
                          }}
                          title={item.quantity <= step ? "Cantidad mínima" : "Disminuir cantidad"}
                        >
                          <Minus size={14} />
                        </button>
                        <QuantityInput
                          item={{ ...item, isFractional: isFrac }}
                          value={item.quantity}
                          isFractional={isFrac}
                          unitOfMeasure={item.unitOfMeasure}
                          onChange={(newQty) => handleUpdateQuantity(idx, newQty)}
                          onUpdateQty={(_, newQty) => handleUpdateQuantity(idx, newQty)}
                        />
                        <button
                          type="button"
                          className="qty-btn"
                          onClick={() => {
                            const newQty = Math.round(((Number(item.quantity) || 0) + step) * 1000) / 1000;
                            handleUpdateQuantity(idx, newQty);
                          }}
                          title="Aumentar cantidad"
                        >
                          <Plus size={14} />
                        </button>
                      </div>
                    </td>

                    <td className="text-right">{formatNumberEs(unitBsS)}</td>

                    <td className="text-right font-bold color-primary">
                      {formatNumberEs(subtotalBsS)}
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
        <div className="alert alert-warning mb-3 flex-align-center gap-2 es-warning">
          <AlertTriangle size={18} className="flex-shrink-0" />
          <span>El nuevo total ({formatUSD(newTotalUSD)}) no puede ser menor a lo ya abonado por el cliente ({formatUSD(totalPaidUSD)}).</span>
        </div>
      )}

      {/* Resumen Financiero */}
      <div 
        className="border text-sm mb-4 es-summary-box"
      >
        <div className="flex-between mb-1">
          <span className="text-muted">Nuevo Total:</span>
                <span className="font-bold"><span className="color-primary font-bold es-total-bs">{formatBsS(newTotalBsS)}</span> <span className="text-muted text-xs font-normal">({formatUSD(newTotalEffectiveUsd)})</span></span>
        </div>
        <div className="flex-between mb-1">
          <span className="text-muted">Total Ya Abonado:</span>
          <span className="font-bold text-success"><span className="es-total-bs">{formatBsS(totalPaidBsS)}</span> <span className="text-muted text-xs font-normal">({formatUSD(totalPaidUSD)})</span></span>
        </div>
        <div className="flex-between mb-1">
          <span className="text-muted">Nuevo Saldo Restante:</span>
                  <span className="font-bold text-danger"><span className="es-total-bs">{formatBsS(newRemainingBalanceBsS)}</span> <span className="text-muted text-xs font-normal">({formatUSD(newRemainingEffectiveUsd)})</span></span>
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
