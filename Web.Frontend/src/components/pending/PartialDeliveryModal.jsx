import React, { useCallback, useEffect, useRef, useState } from 'react';
import { CheckCircle, Loader2, PackageCheck } from 'lucide-react';
import { buildDeliveryPayload, clampQuantity, isConfirmEnabled } from '../../utils/deliveryProgress';
import { formatQuantity } from '../../utils/formatters';
import { createCheckoutKeyHolder } from '../../utils/idempotency.js';
import Modal from '../ui/Modal';

function quantityOrZero(value) {
  const quantity = Number(value);
  return Number.isFinite(quantity) && quantity > 0 ? quantity : 0;
}

function createDeliveryRows(items = [], prefillPending = false) {
  return (Array.isArray(items) ? items : []).map((item) => {
    const totalQuantity = quantityOrZero(item.totalQuantity ?? item.quantity);
    const deliveredQuantity = quantityOrZero(item.deliveredQuantity);
    const pendingQuantity = quantityOrZero(item.pendingQuantity ?? Math.max(0, totalQuantity - deliveredQuantity));
    const saleItemId = item.saleItemId ?? item.id;

    return {
      saleItemId,
      productName: item.displayProductName || item.productName || 'Producto',
      totalQuantity,
      deliveredQuantity,
      pendingQuantity,
      quantity: prefillPending ? pendingQuantity : 0,
      isInvalid: false,
    };
  });
}

function reconcileDeliveryRows(items, previousRows, prefillPending) {
  const previousById = new Map(previousRows.map((row) => [row.saleItemId, row]));
  return createDeliveryRows(items, prefillPending).map((row) => {
    const previous = previousById.get(row.saleItemId);
    if (!previous) return row;

    const clamped = clampQuantity(previous.quantity, row.pendingQuantity);
    return {
      ...row,
      quantity: clamped.value,
      isInvalid: clamped.isInvalid,
    };
  });
}

export default function PartialDeliveryModal({ pickup, onClose, onConfirm, onRejected, prefillPending = false }) {
  const [rows, setRows] = useState(() => createDeliveryRows(pickup?.items, prefillPending));
  const [notes, setNotes] = useState('');
  const [requestError, setRequestError] = useState(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const idempotencyKeyHolderRef = useRef(null);
  const isSubmittingRef = useRef(false);

  if (!idempotencyKeyHolderRef.current) {
    idempotencyKeyHolderRef.current = createCheckoutKeyHolder();
  }

  useEffect(() => {
    setRows((previousRows) => reconcileDeliveryRows(pickup?.items, previousRows, prefillPending));
  }, [pickup, prefillPending]);

  const handleClose = useCallback(() => {
    if (!isSubmittingRef.current) onClose?.();
  }, [onClose]);

  const handleQuantityChange = (saleItemId, value) => {
    setRequestError(null);
    setRows((currentRows) => currentRows.map((row) => {
      if (row.saleItemId !== saleItemId) return row;
      const clamped = clampQuantity(value, row.pendingQuantity);
      return { ...row, quantity: clamped.value, isInvalid: clamped.isInvalid };
    }));
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    if (isSubmitting || !isConfirmEnabled(rows)) return;

    setRequestError(null);
    setIsSubmitting(true);
    isSubmittingRef.current = true;

    try {
      const items = buildDeliveryPayload(rows);
      await onConfirm({
        saleId: pickup.saleId,
        items,
        notes: notes.trim() || null,
        idempotencyKey: idempotencyKeyHolderRef.current.getOrCreateKey(),
      });
      idempotencyKeyHolderRef.current.reset();
      isSubmittingRef.current = false;
      onClose?.();
    } catch (error) {
      setRequestError(error?.message || 'No se pudo registrar el retiro. Revise los datos e intente de nuevo.');
      setIsSubmitting(false);
      isSubmittingRef.current = false;

      if ([400, 409].includes(error?.status)) {
        try {
          await onRejected?.(error, pickup.saleId);
        } catch (refreshError) {
          console.error('[PartialDeliveryModal] Error al actualizar retiros pendientes:', refreshError);
        }
      }
    }
  };

  return (
    <Modal isOpen onClose={handleClose} title="Despachar mercancía">
      <form className="ppd-form" onSubmit={handleSubmit}>
        <div className="ppd-heading">
          <PackageCheck size={22} aria-hidden="true" />
          <div>
            <p className="ppd-invoice">Factura N° {pickup.invoiceNumber || pickup.saleId}</p>
            <p className="ppd-customer">{pickup.customerName || 'Consumidor Final'}</p>
          </div>
        </div>

        <p className="ppd-instructions">Indique la cantidad que se retira hoy de cada producto.</p>

        <fieldset className="ppd-items">
          <legend className="ppd-visually-hidden">Productos pendientes de despacho</legend>
          {rows.map((row, index) => {
            const inputId = `partial-delivery-quantity-${pickup.saleId}-${row.saleItemId ?? index}`;
            const pendingId = `${inputId}-pending`;
            const errorId = `${inputId}-error`;

            return (
              <div className="ppd-item" key={row.saleItemId ?? index}>
                <div className="ppd-item-details">
                  <strong className="ppd-product-name">{row.productName}</strong>
                  <span id={pendingId} className="ppd-pending-quantity">
                    Pendiente: {formatQuantity(row.pendingQuantity)}
                  </span>
                </div>
                <div className="ppd-quantity-field">
                  <label htmlFor={inputId}>Cantidad a retirar hoy</label>
                  <input
                    id={inputId}
                    className="input-field ppd-quantity-input"
                    type="number"
                    min="0"
                    max={row.pendingQuantity}
                    step="any"
                    value={row.quantity}
                    aria-invalid={row.isInvalid}
                    aria-describedby={`${pendingId}${row.isInvalid ? ` ${errorId}` : ''}`}
                    disabled={isSubmitting || row.pendingQuantity <= 0}
                    onChange={(event) => handleQuantityChange(row.saleItemId, event.target.value)}
                  />
                  {row.isInvalid && (
                    <span id={errorId} className="ppd-field-error" role="alert">
                      La cantidad debe estar entre 0 y {formatQuantity(row.pendingQuantity)}.
                    </span>
                  )}
                </div>
              </div>
            );
          })}
        </fieldset>

        <div className="ppd-notes-field">
          <label htmlFor={`partial-delivery-notes-${pickup.saleId}`}>Notas (opcional)</label>
          <textarea
            id={`partial-delivery-notes-${pickup.saleId}`}
            className="input-field ppd-notes-input"
            rows="3"
            value={notes}
            disabled={isSubmitting}
            onChange={(event) => {
              setNotes(event.target.value);
              setRequestError(null);
            }}
          />
        </div>

        {requestError && (
          <p className="ppd-request-error" role="alert" aria-live="assertive">
            {requestError}
          </p>
        )}

        <div className="ppd-actions">
          <button type="button" className="btn btn-outline" onClick={handleClose} disabled={isSubmitting}>
            Cancelar
          </button>
          <button
            type="submit"
            className="btn btn-primary ppd-submit-button"
            disabled={isSubmitting || !isConfirmEnabled(rows)}
          >
            {isSubmitting ? (
              <>
                <Loader2 className="animate-spin" size={18} aria-hidden="true" /> Procesando...
              </>
            ) : (
              <>
                <CheckCircle size={18} aria-hidden="true" /> Confirmar Retiro
              </>
            )}
          </button>
        </div>
      </form>
    </Modal>
  );
}
