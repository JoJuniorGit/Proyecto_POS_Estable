import { useCallback, useEffect, useId, useMemo, useState } from 'react';
import Modal from '../ui/Modal';
import { getCreditReceivables, registerCreditPayment } from '../../services/creditApi';
import { getActivePaymentMethods } from '../../services/paymentApi';
import {
  creditPaymentRemaining,
  formatDateVe,
  isReceivableOpen,
  parseCreditAmount,
  receivableBsSProyeccion,
  validateCreditPaymentInput,
} from './creditLogic';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { resolveLocalCurrencyProfile } from '../register/registerFormLogic';
import { AlertCircle, CheckCircle2, Loader2 } from 'lucide-react';

/**
 * 8.211-T4b2b2b (S9): etiquetas del perfil monetario activo para el abono CxC. La deuda se
 * denomina en la primaria; la proyección local sólo se muestra en `Dual`.
 * @param {{ mode?: string, primaryCurrencyCode?: string, secondaryCurrencyCode?: string }} profile
 */
export function resolveCreditPaymentCurrencyLabels(profile) {
  const local = resolveLocalCurrencyProfile(profile);
  return {
    isDual: local.isDual,
    primaryLabel: displayCurrencyCode(profile?.primaryCurrencyCode) || 'USD',
    localLabel: local.label || 'Bs.S',
  };
}

/**
 * 8.187-T5 (S3/S6): modal de abono CxC del POS web. El operador captura el monto en la moneda
 * primaria del perfil (la deuda está denominada en ella) y, en `Dual`, ve la equivalencia
 * informativa en la moneda local a la tasa del día; el servidor congela la tasa del cobro y
 * calcula el FX realizado. La imputación es FIFO de cartera por defecto o explícita por factura.
 * Solo el efectivo ingresa a la gaveta (lo resuelve el server).
 */
export default function CreditPaymentModal({ isOpen, onClose, customerId, customerName, todayRate = 0, onSuccess }) {
  const uid = useId();
  const { profile: monetaryProfile, formatPrimary, formatSecondary } = useMonetaryProfile();
  const { isDual, primaryLabel, localLabel } = resolveCreditPaymentCurrencyLabels(monetaryProfile);
  const [invoices, setInvoices] = useState([]);
  const [methods, setMethods] = useState([]);
  const [isLoading, setIsLoading] = useState(false);
  const [loadError, setLoadError] = useState(null);
  const [amountText, setAmountText] = useState('');
  const [methodId, setMethodId] = useState('');
  const [reference, setReference] = useState('');
  const [allocationMode, setAllocationMode] = useState('fifo');
  const [selectedIds, setSelectedIds] = useState([]);
  const [isProcessing, setIsProcessing] = useState(false);
  const [submitError, setSubmitError] = useState(null);

  const loadData = useCallback(async (id) => {
    setIsLoading(true);
    setLoadError(null);
    try {
      const [book, activeMethods] = await Promise.all([
        getCreditReceivables({ customerId: id }),
        getActivePaymentMethods(),
      ]);
      setInvoices((book?.items || []).filter((item) => isReceivableOpen(item)));
      setMethods(activeMethods || []);
    } catch (err) {
      setInvoices([]);
      setMethods([]);
      setLoadError(err?.message || 'No se pudieron cargar las facturas del cliente.');
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!isOpen) return;
    setAmountText('');
    setMethodId('');
    setReference('');
    setAllocationMode('fifo');
    setSelectedIds([]);
    setSubmitError(null);
    setIsProcessing(false);
    if (customerId) loadData(customerId);
  }, [isOpen, customerId, loadData]);

  const selectedMethod = useMemo(
    () => methods.find((m) => String(m.id) === String(methodId)) || null,
    [methods, methodId]
  );

  const amountUSD = parseCreditAmount(amountText);
  const totalOpenUSD = useMemo(
    () => invoices.reduce((acc, item) => acc + Number(item.balanceUSD || 0), 0),
    [invoices]
  );
  const selectedTotalUSD = useMemo(
    () => invoices
      .filter((item) => selectedIds.includes(item.receivableId))
      .reduce((acc, item) => acc + Number(item.balanceUSD || 0), 0),
    [invoices, selectedIds]
  );

  const isExplicitAllocation = allocationMode === 'explicit';
  const validationMessage = validateCreditPaymentInput({
    amountUSD,
    method: selectedMethod,
    reference,
    isExplicitAllocation,
    selectedIds,
    selectedTotalUSD,
    totalOpenUSD,
  });

  const toggleInvoice = (receivableId) => {
    setSubmitError(null);
    setSelectedIds((prev) => (prev.includes(receivableId)
      ? prev.filter((id) => id !== receivableId)
      : [...prev, receivableId]));
  };

  const handleSubmit = async () => {
    if (isProcessing) return;

    if (validationMessage) {
      setSubmitError(validationMessage);
      return;
    }

    setIsProcessing(true);
    setSubmitError(null);
    try {
      const result = await registerCreditPayment({
        customerId,
        amountUSD,
        paymentMethodId: selectedMethod.id,
        referenceNumber: reference.trim() || null,
        receivableIds: isExplicitAllocation ? selectedIds : [],
      });
      // El padre refresca la cartera y muestra el acuse con la descarga del comprobante.
      onSuccess?.(result);
    } catch (err) {
      setSubmitError(err?.message || 'No se pudo registrar el abono.');
    } finally {
      setIsProcessing(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Registrar Abono CxC" maxWidth="560px">
      <div className="credit-modal-section">
        <p className="text-muted mb-2">
          Cliente: <strong>{customerName || 'Cliente'}</strong> · Saldo pendiente:{' '}
          <strong>{formatPrimary(totalOpenUSD)}</strong>
          {isDual && todayRate > 0 && (
            <span className="text-muted"> (Tasa del día: {formatSecondary(todayRate)})</span>
          )}
        </p>
      </div>

      {loadError && (
        <div className="alert alert-danger credit-modal-message" role="alert">
          <AlertCircle size={16} /> {loadError}
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={() => customerId && loadData(customerId)}
          >
            Reintentar
          </button>
        </div>
      )}

      {isLoading ? (
        <p className="credit-state" role="status">
          <Loader2 size={18} className="animate-spin" /> Cargando facturas del cliente...
        </p>
      ) : (
        <>
          <div className="credit-modal-section form-group">
            <label htmlFor={`${uid}-amount`}>Monto del abono ({primaryLabel})</label>
            <input
              id={`${uid}-amount`}
              type="text"
              inputMode="decimal"
              className="form-control"
              placeholder="0.00"
              value={amountText}
              onChange={(e) => {
                setAmountText(e.target.value);
                setSubmitError(null);
              }}
              onFocus={(e) => e.target.select()}
              disabled={isProcessing}
            />
          </div>

          <div className="credit-amount-preview credit-modal-section">
            {isDual && (
              <div className="credit-amount-preview-row">
                <span>Equivalente {localLabel} (tasa del día, informativo):</span>
                <strong>{formatSecondary(receivableBsSProyeccion(amountUSD, todayRate))}</strong>
              </div>
            )}
            <div className="credit-amount-preview-row">
              <span>Saldo resultante del cliente:</span>
              <strong>{formatPrimary(creditPaymentRemaining(totalOpenUSD, amountUSD))}</strong>
            </div>
          </div>

          <div className="credit-modal-section form-group">
            <label htmlFor={`${uid}-method`}>Método de pago</label>
            <select
              id={`${uid}-method`}
              className="form-select"
              value={methodId}
              onChange={(e) => {
                setMethodId(e.target.value);
                setSubmitError(null);
              }}
              disabled={isProcessing || methods.length === 0}
            >
              <option value="">Seleccione...</option>
              {methods.map((method) => (
                <option key={method.id} value={method.id}>
                  {method.name}{method.isCash ? ' (Efectivo)' : ''}
                </option>
              ))}
            </select>
          </div>

          {selectedMethod?.requiresReference && (
            <div className="credit-modal-section form-group">
              <label htmlFor={`${uid}-reference`}>Número de referencia *</label>
              <input
                id={`${uid}-reference`}
                type="text"
                className="form-control"
                value={reference}
                onChange={(e) => {
                  setReference(e.target.value);
                  setSubmitError(null);
                }}
                disabled={isProcessing}
              />
            </div>
          )}

          <div className="credit-modal-section">
            <h4>Imputación del abono</h4>
            <div className="credit-profile-status-buttons" role="radiogroup" aria-label="Modo de imputación">
              <button
                type="button"
                className={`btn ${allocationMode === 'fifo' ? 'btn-primary' : 'btn-outline'}`}
                onClick={() => setAllocationMode('fifo')}
                aria-pressed={allocationMode === 'fifo'}
                disabled={isProcessing}
              >
                FIFO (más antigua primero)
              </button>
              <button
                type="button"
                className={`btn ${allocationMode === 'explicit' ? 'btn-primary' : 'btn-outline'}`}
                onClick={() => setAllocationMode('explicit')}
                aria-pressed={allocationMode === 'explicit'}
                disabled={isProcessing}
              >
                Facturas específicas
              </button>
            </div>
          </div>

          {isExplicitAllocation && (
            <div className="credit-modal-section">
              {invoices.length === 0 ? (
                <p className="text-muted">El cliente no tiene facturas abiertas para imputar.</p>
              ) : (
                <div className="credit-invoice-option-list" role="group" aria-label="Facturas abiertas del cliente">
                  {invoices.map((invoice) => {
                    const isSelected = selectedIds.includes(invoice.receivableId);
                    return (
                      <label
                        key={invoice.receivableId}
                        className={`credit-invoice-option${isSelected ? ' credit-invoice-option--selected' : ''}`}
                      >
                        <input
                          type="checkbox"
                          checked={isSelected}
                          onChange={() => toggleInvoice(invoice.receivableId)}
                          disabled={isProcessing}
                        />
                        <span className="credit-invoice-option-info">
                          <span>
                            {invoice.invoiceNumber ? `F${String(invoice.invoiceNumber).padStart(5, '0')}` : `V${invoice.saleId}`}
                            {' '}· Vence: {formatDateVe(invoice.dueDateUtc)}
                          </span>
                          <span>{formatPrimary(invoice.balanceUSD)}</span>
                        </span>
                      </label>
                    );
                  })}
                </div>
              )}
              <p className="text-muted mt-1 credit-profile-readonly-note">
                Seleccionadas: {formatPrimary(selectedTotalUSD)}
              </p>
            </div>
          )}

          {submitError && (
            <div className="alert alert-danger credit-modal-message" role="alert">
              <AlertCircle size={16} /> {submitError}
            </div>
          )}

          {!submitError && validationMessage && amountText && (
            <p className="text-muted credit-profile-readonly-note">{validationMessage}</p>
          )}

          <div className="credit-modal-actions">
            <button type="button" className="btn btn-outline" onClick={onClose} disabled={isProcessing}>
              Cancelar
            </button>
            <button type="button" className="btn btn-primary" onClick={handleSubmit} disabled={isProcessing}>
              {isProcessing ? (
                <><Loader2 size={16} className="animate-spin" /> Registrando...</>
              ) : (
                <><CheckCircle2 size={16} /> Registrar Abono</>
              )}
            </button>
          </div>
        </>
      )}
    </Modal>
  );
}
