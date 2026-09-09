import React, { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { ShieldCheck, AlertCircle } from 'lucide-react';
import AtmAmountInput from '../ui/AtmAmountInput';
import { formatBsS, formatUSD, formatNumberEs } from '../../utils/formatters';
import './PartialPaymentModal.css';

export default function PartialPaymentModal({ isOpen, onClose, onConfirmPayment, sale, exchangeRate, paymentMethods = [] }) {
  const [amountBsS, setAmountBsS] = useState(0);
  const [amountUSD, setAmountUSD] = useState(0);
  const [paymentMethodId, setPaymentMethodId] = useState(paymentMethods[0]?.id || 1);
  const [referenceNumber, setReferenceNumber] = useState('');
  const [error, setError] = useState(null);

  useEffect(() => {
    if (isOpen) {
      setAmountBsS(0);
      setAmountUSD(0);
      setReferenceNumber('');
      setError(null);
      if (paymentMethods.length > 0) setPaymentMethodId(paymentMethods[0].id);
    }
  }, [isOpen, paymentMethods]);

  const selectedMethod = paymentMethods.find((m) => String(m.id) === String(paymentMethodId));
  const isCashSelected = !!selectedMethod?.isCash;

  const handleBsSChange = (numericVal) => {
    setAmountBsS(numericVal);
    if (exchangeRate > 0) {
      setAmountUSD(numericVal / exchangeRate);
    }
  };

  const handleUSDChange = (numericVal) => {
    setAmountUSD(numericVal);
    if (exchangeRate > 0) {
      // El efectivo solo acepta montos enteros: el equivalente en Bs.S se trunca sin centavos.
      setAmountBsS(isCashSelected ? Math.trunc(numericVal * exchangeRate) : numericVal * exchangeRate);
    }
  };

  const usdValue = amountUSD || 0;
  const bsValue = amountBsS || 0;
  const remainingUsd = (sale?.remainingBalanceUSD || 0) - usdValue;

  const handleSubmit = (e) => {
    e.preventDefault();
    setError(null);

    if (usdValue <= 0 || bsValue <= 0) {
      setError('Ingresa un monto mayor a cero.');
      return;
    }

    if (isCashSelected && Math.abs(bsValue - Math.round(bsValue)) >= 0.0001) {
      setError('El pago en efectivo solo acepta números enteros (ej: 10 o 10.00). Montos con centavos como 15.01 no son permitidos.');
      return;
    }

    // 8.7-M13: un pago por método no-efectivo (zelle/punto/transferencia) exige su referencia.
    if (!isCashSelected && !referenceNumber.trim()) {
      setError('Ingrese el número de referencia / recibo para métodos de pago no-efectivos.');
      return;
    }

    if (usdValue > (sale?.remainingBalanceUSD || 0) + 0.01) {
      setError(`El abono (${formatUSD(usdValue)}) no puede ser mayor que la deuda pendiente (${formatUSD(sale?.remainingBalanceUSD || 0)}).`);
      return;
    }

    onConfirmPayment({
      paymentMethodId: parseInt(paymentMethodId),
      amountBsS: isCashSelected ? Math.trunc(bsValue) : bsValue,
      amountUSD: isCashSelected ? Math.trunc(bsValue) / exchangeRate : usdValue,
      exchangeRate: exchangeRate,
      referenceNumber: referenceNumber,
    });
  };

  if (!isOpen) return null;

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Registrar Abono" maxWidth="520px">
      <form onSubmit={handleSubmit}>
          <div className="pp-form-body d-flex flex-column gap-4">
            {error && (
              <div className="alert alert-danger pp-alert-error d-flex align-center gap-2">
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            {/* Info header */}
            <div className="pp-balance-box d-flex justify-between align-center flex-wrap gap-2 p-3">
              <div>
                <span>Tasa Activa: <strong>{formatNumberEs(exchangeRate)} Bs.S / USD</strong></span>
              </div>
              <div>
                <span>Saldo Pendiente: </span>
                <strong className="pp-danger-amount">{formatBsS((sale?.remainingBalanceUSD || 0) * exchangeRate)}</strong>
                <span className="text-muted pp-muted-note">({formatUSD(sale?.remainingBalanceUSD || 0)})</span>
              </div>
            </div>

            {/* Amount inputs */}
            <div className="grid grid-2 gap-3">
              <div>
                <label className="input-label">Monto en Bolívares (Bs.S) *</label>
                <AtmAmountInput
                  value={amountBsS}
                  onChange={handleBsSChange}
                  placeholder={isCashSelected ? '0' : '0,00'}
                  allowDecimals={!isCashSelected}
                />
                {isCashSelected && (
                  <small className="d-block mt-1 text-xs pp-cash-hint">
                    El pago en efectivo solo acepta montos enteros.
                  </small>
                )}
              </div>

              <div>
                <label className="input-label">Equivalente USD ($) *</label>
                <AtmAmountInput
                  value={amountUSD}
                  onChange={handleUSDChange}
                  placeholder="0,00"
                />
              </div>
            </div>

            {/* Payment Method & Reference */}
            <div className="grid grid-2 gap-3">
              <div>
                <label className="input-label">Método de Pago *</label>
                <select
                  className="input-field"
                  value={paymentMethodId}
                  onChange={(e) => setPaymentMethodId(e.target.value)}
                >
                  {paymentMethods.map((m) => (
                    <option key={m.id} value={m.id}>{m.name}</option>
                  ))}
                </select>
              </div>

              <div>
                <label className="input-label">Nº Referencia / Recibo</label>
                <input
                  type="text"
                  className="input-field"
                  placeholder="Ej: Ref #12345"
                  value={referenceNumber}
                  onChange={(e) => setReferenceNumber(e.target.value)}
                />
              </div>
            </div>

            {/* Protection Guarantee Notice */}
            <div className="pp-guarantee-box d-flex align-center p-3">
              <ShieldCheck size={28} className="pp-shield-icon flex-shrink-0" />
              <div className="pp-guarantee-text">
                <strong>Regla Anti-Devaluación:</strong> Este abono de <strong className="color-primary">{formatBsS(bsValue)}</strong> equivale a <strong>{formatUSD(usdValue)}</strong> al cambio actual. La deuda restante del cliente bajará a <strong className="color-danger">{formatBsS(Math.max(0, remainingUsd) * exchangeRate)}</strong> ({formatUSD(Math.max(0, remainingUsd))}).
              </div>
            </div>
          </div>

          <div className="modal-footer pp-modal-footer d-flex justify-end">
            <button type="button" className="btn btn-outline" onClick={onClose}>Cancelar</button>
            <button type="submit" className="btn btn-primary" disabled={usdValue <= 0 || remainingUsd < -0.01 || (isCashSelected && bsValue % 1 !== 0)}>
              Confirmar Abono
            </button>
          </div>
        </form>
    </Modal>
  );
}
