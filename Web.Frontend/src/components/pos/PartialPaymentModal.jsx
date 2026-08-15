import React, { useState, useEffect } from 'react';
import { X, ShieldCheck, DollarSign, Calculator, AlertCircle } from 'lucide-react';
import AtmAmountInput from '../ui/AtmAmountInput';
import { formatBsS, formatUSD, formatNumberEs } from '../../utils/formatters';

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
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-container card" style={{ maxWidth: '520px', padding: 0, overflow: 'hidden' }} onClick={(e) => e.stopPropagation()}>
        <div className="modal-header" style={{ padding: '16px 20px', borderBottom: '1px solid var(--border-color)', margin: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <Calculator size={20} style={{ color: 'var(--primary-color)' }} />
            <h3 className="modal-title" style={{ margin: 0 }}>Registrar Abono</h3>
          </div>
          <button type="button" className="modal-close-btn" onClick={onClose}>
            <X size={18} />
          </button>
        </div>

        <form onSubmit={handleSubmit}>
          <div style={{ padding: '20px', display: 'flex', flexDirection: 'column', gap: '16px' }}>
            {error && (
              <div className="alert alert-danger" style={{ fontSize: '0.85em', display: 'flex', alignItems: 'center', gap: '8px' }}>
                <AlertCircle size={16} />
                <span>{error}</span>
              </div>
            )}

            {/* Info header */}
            <div style={{ padding: '12px', borderRadius: '8px', backgroundColor: 'var(--bg-tertiary, rgba(128,128,128,0.1))', fontSize: '0.85em', display: 'flex', justifyContent: 'space-between', flexWrap: 'wrap', gap: '8px' }}>
              <div>
                <span>Tasa Activa: <strong>{formatNumberEs(exchangeRate)} Bs/$</strong></span>
              </div>
              <div>
                <span>Saldo Pendiente: <span style={{ color: '#ef4444' }}>{formatUSD(sale?.remainingBalanceUSD || 0)}</span></span>
                <span> ({formatBsS((sale?.remainingBalanceUSD || 0) * exchangeRate)})</span>
              </div>
            </div>

            {/* Amount inputs */}
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
              <div>
                <label className="input-label">Monto en Bolívares (Bs.S) *</label>
                <AtmAmountInput
                  value={amountBsS}
                  onChange={handleBsSChange}
                  placeholder={isCashSelected ? '0' : '0,00'}
                  allowDecimals={!isCashSelected}
                />
                {isCashSelected && (
                  <small className="text-muted" style={{ display: 'block', marginTop: '4px', fontSize: '0.75rem', color: 'var(--accent-primary, #6366f1)' }}>
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
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '12px' }}>
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
            <div style={{ padding: '12px', borderRadius: '8px', backgroundColor: 'rgba(99, 102, 241, 0.12)', border: '1px solid rgba(99, 102, 241, 0.3)', display: 'flex', gap: '10px', alignItems: 'center' }}>
              <ShieldCheck size={28} style={{ color: '#6366f1', flexShrink: 0 }} />
              <div style={{ fontSize: '0.85em', color: 'var(--text-color)' }}>
                <strong>Regla Anti-Devaluación:</strong> Este abono en Bolívares se convierte automáticamente a <strong>{formatUSD(usdValue)}</strong> al cambio actual. La deuda restante del cliente bajará a <strong>{formatUSD(Math.max(0, remainingUsd))}</strong>.
              </div>
            </div>
          </div>

          <div className="modal-footer" style={{ display: 'flex', justifyContent: 'flex-end', gap: '10px', padding: '15px 20px', borderTop: '1px solid var(--border-color)' }}>
            <button type="button" className="btn btn-outline" onClick={onClose}>Cancelar</button>
            <button type="submit" className="btn btn-primary" disabled={usdValue <= 0 || remainingUsd < -0.01 || (isCashSelected && bsValue % 1 !== 0)}>
              Confirmar Abono
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
