import { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { FastForward, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react';
import { api } from '../../services/api';
import { getActivePaymentMethods } from '../../services/paymentApi';
import {
  buildCashAdvancePayload,
  formatPhysicalMethodAmount,
  processCashAdvance,
  resolveDispensedMethod,
  validateDispensedBalance,
} from '../../services/cashDrawerApi';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { resolveLocalCurrencyProfile } from './registerFormLogic';
import { resolveCashAmountRules } from '../checkout/PaymentForm';
import './RegisterModals.css';

const COMMISSION_UNAVAILABLE_MESSAGE = 'No se pudo obtener la comisión configurada del servidor. No se puede procesar el adelanto.';

export function resolveCommissionPercentage(response) {
  const percentage = response?.percentage;
  return typeof percentage === 'number' && Number.isFinite(percentage) && percentage > 0 ? percentage : null;
}

export function computeAdvanceSummary({ amountBsS, percentage, exchangeRate }) {
  const hasResolvedCommission = typeof percentage === 'number' && Number.isFinite(percentage) && percentage > 0;
  const numRequested = Number.isFinite(amountBsS) ? amountBsS : 0;

  if (!hasResolvedCommission) {
    return { hasResolvedCommission: false, commissionBsS: null, totalChargedBsS: null, totalChargedUsd: null };
  }

  const commissionBsS = Math.round((numRequested * (percentage / 100) + Number.EPSILON) * 100) / 100;
  const totalChargedBsS = Math.round((numRequested + commissionBsS + Number.EPSILON) * 100) / 100;
  const totalChargedUsd = exchangeRate > 0 ? totalChargedBsS / exchangeRate : 0;

  return { hasResolvedCommission: true, commissionBsS, totalChargedBsS, totalChargedUsd };
}

export default function CashAdvanceModal({
  isOpen,
  onClose,
  sessionId,
  availableCashBsS,
  exchangeRate,
  user,
  physicalMethodBalances = [],
  onSuccess,
}) {
  const {
    profile,
    mode,
    secondaryCurrencyCode,
    formatPrimary,
    formatSecondary,
  } = useMonetaryProfile();
  const { isDual, code: localCurrencyCode, label: localCurrencyLabel } = resolveLocalCurrencyProfile(profile);
  const { requiresIntegerCash } = resolveCashAmountRules({ mode, secondaryCurrencyCode });

  const [amountBsS, setAmountBsS] = useState('');
  const [paymentMethods, setPaymentMethods] = useState([]);
  const [selectedMethodId, setSelectedMethodId] = useState('');
  const [selectedDispensedMethodId, setSelectedDispensedMethodId] = useState('');
  const [commissionPercentage, setCommissionPercentage] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [successInfo, setSuccessInfo] = useState(null);

  useEffect(() => {
    if (!isOpen) return undefined;
    let cancelled = false;
    getActivePaymentMethods()
      .then((methods) => {
        if (cancelled) return;
        // Filter out physical cash
        const electronic = (methods || []).filter(m => !m.isCash && m.name.toLowerCase() !== 'efectivo');
        setPaymentMethods(electronic);
        if (electronic.length > 0) {
          setSelectedMethodId((prev) => (prev && electronic.some(m => String(m.id) === String(prev)) ? prev : String(electronic[0].id)));
        }
      })
      .catch(err => {
        if (!cancelled) console.error('Error cargando métodos de pago:', err);
      });
    return () => {
      cancelled = true;
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    const methods = Array.isArray(physicalMethodBalances) ? physicalMethodBalances : [];
    const preferred = resolveDispensedMethod(methods, '');
    setSelectedDispensedMethodId(preferred ? String(preferred.paymentMethodId) : '');
  }, [isOpen, physicalMethodBalances]);

  const handleClose = () => {
    setAmountBsS('');
    setError('');
    setSuccessInfo(null);
    onClose();
  };

  const selectedMethod = paymentMethods.find(m => String(m.id) === String(selectedMethodId));
  const methodName = selectedMethod?.name || '';
  const isTransfer = methodName.toLowerCase().includes('transfer') || 
                     methodName.toLowerCase().includes('pago m�vil') || 
                     methodName.toLowerCase().includes('pago movil');

  const physicalMethods = Array.isArray(physicalMethodBalances) ? physicalMethodBalances : [];
  const hasPhysicalMethods = physicalMethods.length > 0;
  const selectedDispensedMethod = resolveDispensedMethod(physicalMethods, selectedDispensedMethodId);
  const dispensedCurrencyLabel = selectedDispensedMethod?.currencyCode || localCurrencyLabel;
  const dispensedRequiresInteger = requiresIntegerCash
    && String(selectedDispensedMethod?.currencyCode || localCurrencyCode).toUpperCase()
      === String(localCurrencyCode).toUpperCase();
  const formatDispensed = (value) => selectedDispensedMethod
    ? formatPhysicalMethodAmount(selectedDispensedMethod, value)
    : formatSecondary(value);
  const availableDisplay = selectedDispensedMethod
    ? formatPhysicalMethodAmount(selectedDispensedMethod, selectedDispensedMethod.currentBalance)
    : formatSecondary(availableCashBsS);
  const projectedDispensedBalance = selectedDispensedMethod
    ? Number(selectedDispensedMethod.currentBalance) - (parseFloat(amountBsS) || 0)
    : null;
  const projectedDispensedDisplay = selectedDispensedMethod
    ? `Existencia actual: ${formatPhysicalMethodAmount(selectedDispensedMethod, selectedDispensedMethod.currentBalance)} -> Existencia tras el avance: ${formatPhysicalMethodAmount(selectedDispensedMethod, projectedDispensedBalance)}`
    : '';

  useEffect(() => {
    if (!isOpen || !selectedMethodId) return undefined;

    let cancelled = false;
    setCommissionPercentage(null);

    api.get(`/api/cashdrawer/advance-commission?isTransfer=${isTransfer}`)
      .then((response) => {
        if (cancelled) return;
        setCommissionPercentage(resolveCommissionPercentage(response));
      })
      .catch(() => {
        if (cancelled) return;
        setCommissionPercentage(null);
      });

    return () => { cancelled = true; };
  }, [isOpen, isTransfer, selectedMethodId]);

  const numRequested = parseFloat(amountBsS) || 0;
  const { hasResolvedCommission, commissionBsS, totalChargedBsS, totalChargedUsd } = computeAdvanceSummary({
    amountBsS: numRequested,
    percentage: commissionPercentage,
    exchangeRate,
  });

  const handleKeyDown = (e) => {
    const blocked = dispensedRequiresInteger ? ['.', ',', 'e', 'E', '+', '-'] : ['e', 'E', '+', '-'];
    if (blocked.includes(e.key)) {
      e.preventDefault();
    }
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError('');
    setSuccessInfo(null);

    if (!sessionId) {
      setError('No se detectó una sesión de caja activa. Por favor actualice la página.');
      return;
    }

    if (numRequested <= 0) {
      setError('Ingrese un monto mayor a cero.');
      return;
    }

    if (dispensedRequiresInteger && (!Number.isInteger(numRequested) || numRequested % 1 !== 0)) {
      setError(`El monto de efectivo a entregar en ${dispensedCurrencyLabel} debe ser un número entero estrictamente sin decimales.`);
      return;
    }

    if (hasPhysicalMethods) {
      const dispensedError = validateDispensedBalance(selectedDispensedMethod, numRequested);
      if (dispensedError) {
        setError(dispensedError);
        return;
      }
    } else if (numRequested > availableCashBsS) {
      setError(`El monto solicitado (${formatSecondary(numRequested)}) supera el efectivo disponible en caja (${formatSecondary(availableCashBsS)}).`);
      return;
    }

    if (!selectedMethod) {
      setError('Seleccione un método de pago electrónico.');
      return;
    }

    if (!hasResolvedCommission) {
      setError(COMMISSION_UNAVAILABLE_MESSAGE);
      return;
    }

    setLoading(true);
    try {
      const res = await processCashAdvance(buildCashAdvancePayload({
        sessionId,
        requestedAmountLocal: numRequested,
        paymentMethodId: selectedMethod.id,
        paymentMethodName: selectedMethod.name,
        isTransfer,
        exchangeRate: exchangeRate || 1,
        cashierId: user?.id,
        userName: user?.name || user?.fullName || 'Usuario',
        dispensedCashPaymentMethodId: selectedDispensedMethod?.paymentMethodId ?? null,
        dispensedAmount: selectedDispensedMethod ? numRequested : null,
        receivedPaymentMethodId: selectedMethod.id,
      }));

      const invoiceMsg = res?.invoiceNumber ? ` (Factura N° ${res.invoiceNumber})` : '';
      setSuccessInfo(`Adelanto de ${formatDispensed(numRequested)} procesado con éxito${invoiceMsg}. Registrado en Historial de Ventas.`);
      
      setTimeout(() => {
        handleClose();
        if (onSuccess) onSuccess();
      }, 1800);
    } catch (err) {
      console.error('Error procesando adelanto:', err);
      setError(typeof err === 'string' ? err : err.message || 'Error al procesar el adelanto de efectivo.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="Adelanto de Efectivo (Cobro Electrónico)" maxWidth="520px">
      {successInfo ? (
        <div className="p-4 text-center">
          <CheckCircle2 size={48} className="color-success mb-2 inline-block animate-bounce" />
          <h4 className="font-bold text-lg mb-2">¡Operación Completada!</h4>
          <p className="text-muted text-sm">{successInfo}</p>
        </div>
      ) : (
        <form onSubmit={handleSubmit} className="cash-advance-form">
          <div className="card p-3 mb-3 bg-light border flex-between">
            <span className="text-xs font-bold text-muted uppercase">Disponible en método físico:</span>
            <span className="font-bold text-lg color-primary">{availableDisplay}</span>
          </div>

          {error && (
            <div className="alert alert-danger mb-3 flex-align-center gap-2">
              <AlertCircle size={18} />
              <span>{error}</span>
            </div>
          )}

          {!hasResolvedCommission && (
            <div className="alert alert-warning mb-3 text-sm">
              {COMMISSION_UNAVAILABLE_MESSAGE}
            </div>
          )}

          {hasPhysicalMethods && (
            <div className="form-group mb-3">
              <label className="form-label font-medium">1. Método Físico que Entrega el Efectivo *</label>
              <select
                className="form-select"
                value={selectedDispensedMethodId}
                onChange={(e) => setSelectedDispensedMethodId(e.target.value)}
                required
              >
                {physicalMethods.map((method) => (
                  <option key={method.paymentMethodId} value={method.paymentMethodId}>
                    {method.name} ({method.currencyCode}) — Disponible: {formatPhysicalMethodAmount(method, method.currentBalance)}
                  </option>
                ))}
              </select>
              <small className="text-muted text-xs mt-1 block">{projectedDispensedDisplay}</small>
            </div>
          )}

          <div className="form-group mb-3">
            <label className="form-label font-medium">
              {hasPhysicalMethods ? '2' : '1'}. Monto Entregado al Cliente (Efectivo en {dispensedCurrencyLabel}{dispensedRequiresInteger ? ' - Solo Enteros' : ''}) *
            </label>
            <input
              type="number"
              step={dispensedRequiresInteger ? '1' : '0.01'}
              inputMode={dispensedRequiresInteger ? 'numeric' : 'decimal'}
              min={dispensedRequiresInteger ? '1' : '0.01'}
              className="form-control form-control-lg font-bold"
              placeholder="0"
              value={amountBsS}
              onChange={(e) => setAmountBsS(e.target.value)}
              onKeyDown={handleKeyDown}
              required
              autoFocus
            />
            <small className="text-muted text-xs mt-1 block">
              {dispensedRequiresInteger
                ? 'Solo números enteros sin decimales (ej. 10, 50, 100).'
                : 'Puede incluir centavos cuando la moneda local los admite (ej. 10,50).'}
            </small>
          </div>

          <div className="form-group mb-4">
            <label className="form-label font-medium">{hasPhysicalMethods ? '3' : '2'}. Método de Cobro al Cliente (Solo Electrónico) *</label>
            <select
              className="form-select"
              value={selectedMethodId}
              onChange={(e) => setSelectedMethodId(e.target.value)}
              required
            >
              {paymentMethods.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.name}
                </option>
              ))}
            </select>
          </div>

          {/* Resumen Financiero */}
          <div className="card p-3 mb-4 bg-surface border-blue">
            <div className="text-xs font-bold color-primary mb-2 uppercase">Resumen Financiero</div>
            
            <div className="flex-between text-sm mb-1">
              <span className="text-muted">Salida de Efectivo de Caja:</span>
              <span className="font-bold color-danger">-{formatDispensed(numRequested)}</span>
            </div>

            <div className="flex-between text-sm mb-2">
              <span className="text-muted">Comisión de Ganancia {hasResolvedCommission ? `(${commissionPercentage}%)` : ''}:</span>
              <span className="font-bold color-success">{hasResolvedCommission ? `+${formatSecondary(commissionBsS)}` : '—'}</span>
            </div>

            <div className="divider my-2"></div>

            <div className="flex-between flex-align-center">
              <span className="font-bold text-sm">TOTAL A COBRAR AL CLIENTE:</span>
              <div className="text-right">
                <div className="font-bold text-lg color-primary">{hasResolvedCommission ? formatSecondary(totalChargedBsS) : '—'}</div>
                {isDual && (
                  <div className="text-xs text-muted">{hasResolvedCommission ? `(${formatPrimary(totalChargedUsd)})` : ''}</div>
                )}
              </div>
            </div>
          </div>

          <div className="modal-actions flex-center gap-3 pt-2 regmod-actions">
            <button type="button" className="btn btn-outline flex-center regmod-btn-130" onClick={handleClose} disabled={loading}>
              Cancelar
            </button>
            <button type="submit" className="btn btn-primary flex-center gap-2 font-bold regmod-btn-180" disabled={loading || numRequested <= 0 || !hasResolvedCommission || (hasPhysicalMethods && !selectedDispensedMethod)}>
              {loading ? <Loader2 size={16} className="animate-spin" /> : <FastForward size={16} />}
              Procesar Adelanto
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
