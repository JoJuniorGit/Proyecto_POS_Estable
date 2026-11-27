import { useCallback, useEffect, useId, useState } from 'react';
import Modal from '../ui/Modal';
import { getSale } from '../../services/salesApi';
import { registerCreditReturn } from '../../services/creditApi';
import { parseCreditAmount } from './creditLogic';
import { formatNumberEs } from '../../utils/formatters';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../register/registerFormLogic';
import { AlertCircle, Loader2, Undo2 } from 'lucide-react';

/**
 * 8.211-T4b2b2b (S9): etiquetas del perfil monetario activo para la devolución a crédito.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 */
export function resolveCreditReturnCurrencyLabels(profile) {
  const local = resolveLocalCurrencyProfile(profile);
  return {
    isDual: local.isDual,
    primaryLabel: displayCurrencyCode(profile?.primaryCurrencyCode) || 'USD',
    localLabel: local.label || 'Bs.S',
  };
}

/**
 * 8.187-T5 (S5/S7): modal de devolución de una venta a crédito (solo Admin/Manager en la UI).
 * Las cantidades se validan contra lo vendido; el valor por línea y el reembolso del overpago
 * los calcula el servidor sobre la base efectiva (el excedente pagado lo devuelve la caja).
 */
export default function CreditReturnModal({ isOpen, onClose, row, onSuccess }) {
  const uid = useId();
  const { profile: monetaryProfile, formatPrimary } = useMonetaryProfile();
  const { primaryLabel } = resolveCreditReturnCurrencyLabels(monetaryProfile);
  const [sale, setSale] = useState(null);
  const [quantities, setQuantities] = useState({});
  const [reason, setReason] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [loadError, setLoadError] = useState(null);
  const [isProcessing, setIsProcessing] = useState(false);
  const [submitError, setSubmitError] = useState(null);

  const loadSale = useCallback(async (saleId) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const data = await getSale(saleId);
      setSale(data);
      setQuantities({});
    } catch (err) {
      setSale(null);
      setLoadError(err?.message || 'No se pudo cargar la venta a devolver.');
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!isOpen) return;
    setSale(null);
    setQuantities({});
    setReason('');
    setSubmitError(null);
    setIsProcessing(false);
    if (row?.saleId) loadSale(row.saleId);
  }, [isOpen, row?.saleId, loadSale]);

  const lines = sale?.items || [];

  const requestedUSD = lines.reduce((acc, item) => {
    const qty = parseCreditAmount(quantities[item.id]);
    return acc + (qty > 0 ? qty * Number(item.unitPrice || 0) : 0);
  }, 0);

  const validate = () => {
    const requested = lines
      .map((item) => ({ item, quantity: parseCreditAmount(quantities[item.id]) }))
      .filter((entry) => entry.quantity > 0);

    if (requested.length === 0) {
      return 'Seleccione al menos una línea y su cantidad a devolver.';
    }

    const invalid = requested.find((entry) => entry.quantity > Number(entry.item.quantity));
    if (invalid) {
      return `La cantidad a devolver de '${invalid.item.productName}' excede la cantidad vendida (${invalid.item.quantity}).`;
    }

    if (reason.trim().length > 500) {
      return 'El motivo de la devolución no puede exceder 500 caracteres.';
    }

    return null;
  };

  const handleSubmit = async () => {
    if (isProcessing) return;

    const validation = validate();
    if (validation) {
      setSubmitError(validation);
      return;
    }

    const items = lines
      .map((item) => ({ saleItemId: item.id, quantity: parseCreditAmount(quantities[item.id]) }))
      .filter((entry) => entry.quantity > 0);

    setIsProcessing(true);
    setSubmitError(null);
    try {
      const result = await registerCreditReturn(row.saleId, {
        items,
        reason: reason.trim() || null,
      });
      onSuccess?.(result);
    } catch (err) {
      setSubmitError(err?.message || 'No se pudo registrar la devolución.');
    } finally {
      setIsProcessing(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Devolución de Venta a Crédito" maxWidth="680px">
      {loadError ? (
        <div className="alert alert-danger credit-modal-message" role="alert">
          <AlertCircle size={16} /> {loadError}
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={() => row?.saleId && loadSale(row.saleId)}
          >
            Reintentar
          </button>
        </div>
      ) : isLoading ? (
        <p className="credit-state" role="status">
          <Loader2 size={18} className="animate-spin" /> Cargando venta...
        </p>
      ) : sale ? (
        <>
          <div className="credit-modal-section">
            <p className="text-muted">
              Factura: <strong>{row?.invoiceDisplay || `V${row?.saleId}`}</strong> · Cliente:{' '}
              <strong>{row?.customerName || 'Cliente'}</strong> · Saldo vigente: <strong>{formatPrimary(row?.balanceUSD || 0)}</strong>
            </p>
          </div>

          <div className="credit-modal-section">
            <h4>Líneas a devolver</h4>
            <div className="credit-table-wrap">
              <table className="credit-table credit-table--compact">
                <thead>
                  <tr>
                    <th>Producto</th>
                    <th className="text-right">Vendido</th>
                    <th className="text-right">Precio {primaryLabel}</th>
                    <th>Cantidad a devolver</th>
                  </tr>
                </thead>
                <tbody>
                  {lines.map((item) => (
                    <tr key={item.id}>
                      <td>{item.productName}</td>
                      <td className="text-right credit-amount">{formatNumberEs(item.quantity, item.isFractional ? 3 : 0)}</td>
                      <td className="text-right credit-amount">{formatPrimary(item.unitPrice)}</td>
                      <td>
                        <input
                          id={`${uid}-qty-${item.id}`}
                          type="text"
                          inputMode="decimal"
                          className="form-control credit-return-line-qty"
                          placeholder="0"
                          aria-label={`Cantidad a devolver de ${item.productName}`}
                          value={quantities[item.id] ?? ''}
                          onChange={(e) => {
                            setQuantities((prev) => ({ ...prev, [item.id]: e.target.value }));
                            setSubmitError(null);
                          }}
                          disabled={isProcessing}
                        />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className="credit-modal-section">
            <div className="credit-amount-preview">
              <div className="credit-amount-preview-row">
                <span>Valor estimado a devolver ({primaryLabel}):</span>
                <strong>{formatPrimary(requestedUSD)}</strong>
              </div>
              <div className="credit-amount-preview-row text-muted">
                <span>El valor exacto y el reembolso del excedente los calcula el servidor sobre la base efectiva; stock y cupo se restituyen en la misma operación.</span>
              </div>
            </div>
          </div>

          <div className="credit-modal-section form-group">
            <label htmlFor={`${uid}-reason`}>Motivo de la devolución</label>
            <textarea
              id={`${uid}-reason`}
              className="form-control"
              maxLength={500}
              rows={2}
              value={reason}
              onChange={(e) => {
                setReason(e.target.value);
                setSubmitError(null);
              }}
              disabled={isProcessing}
            />
            <small className="form-text text-muted">{reason.length} / 500</small>
          </div>

          {submitError && (
            <div className="alert alert-danger credit-modal-message" role="alert">
              <AlertCircle size={16} /> {submitError}
            </div>
          )}

          <div className="credit-modal-actions">
            <button type="button" className="btn btn-outline" onClick={onClose} disabled={isProcessing}>
              Cancelar
            </button>
            <button type="button" className="btn btn-danger" onClick={handleSubmit} disabled={isProcessing}>
              {isProcessing ? (
                <><Loader2 size={16} className="animate-spin" /> Registrando...</>
              ) : (
                <><Undo2 size={16} /> Registrar Devolución</>
              )}
            </button>
          </div>
        </>
      ) : null}
    </Modal>
  );
}
