import { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { FastForward, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react';
import { api } from '../../services/api';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { formatBsS, formatUSD } from '../../utils/formatters';
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

  const commissionBsS = Math.round(numRequested * (percentage / 100));
  const totalChargedBsS = numRequested + commissionBsS;
  const totalChargedUsd = exchangeRate > 0 ? totalChargedBsS / exchangeRate : 0;

  return { hasResolvedCommission: true, commissionBsS, totalChargedBsS, totalChargedUsd };
}

export default function CashAdvanceModal({ isOpen, onClose, sessionId, availableCashBsS, exchangeRate, user, onSuccess }) {
  const [amountBsS, setAmountBsS] = useState('');
  const [paymentMethods, setPaymentMethods] = useState([]);
  const [selectedMethodId, setSelectedMethodId] = useState('');
  const [commissionPercentage, setCommissionPercentage] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [successInfo, setSuccessInfo] = useState(null);

  useEffect(() => {
    if (isOpen) {
      getActivePaymentMethods()
        .then((methods) => {
          // Filter out physical cash
          const electronic = (methods || []).filter(m => !m.isCash && m.name.toLowerCase() !== 'efectivo');
          setPaymentMethods(electronic);
          if (electronic.length > 0) {
            setSelectedMethodId(String(electronic[0].id));
          }
        })
        .catch(err => console.error('Error cargando métodos de pago:', err));
    }
  }, [isOpen]);

  const handleClose = () => {
    setAmountBsS('');
    setError('');
    setSuccessInfo(null);
    onClose();
  };

  const selectedMethod = paymentMethods.find(m => String(m.id) === String(selectedMethodId));
  const methodName = selectedMethod?.name || '';
  const isTransfer = methodName.toLowerCase().includes('transfer') || 
                     methodName.toLowerCase().includes('pago móvil') || 
                     methodName.toLowerCase().includes('pago movil');

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
    if (e.key === '.' || e.key === ',' || e.key === 'e' || e.key === 'E' || e.key === '+' || e.key === '-') {
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

    if (!Number.isInteger(numRequested) || numRequested % 1 !== 0) {
      setError('El monto de efectivo a entregar debe ser un número entero estrictamente sin decimales.');
      return;
    }

    if (numRequested > availableCashBsS) {
      setError(`El monto solicitado (${formatBsS(numRequested)}) supera el efectivo disponible en caja (${formatBsS(availableCashBsS)}).`);
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
      const res = await api.post('/api/cashdrawer/cash-advance', {
        sessionId,
        requestedAmountLocal: numRequested,
        paymentMethodId: selectedMethod.id,
        paymentMethodName: selectedMethod.name,
        isTransfer,
        exchangeRate: exchangeRate || 1,
        cashierId: user?.id,
        userName: user?.name || user?.fullName || 'Usuario'
      });

      const invoiceMsg = res?.invoiceNumber ? ` (Factura N° ${res.invoiceNumber})` : '';
      setSuccessInfo(`Adelanto de ${formatBsS(numRequested)} procesado con éxito${invoiceMsg}. Registrado en Historial de Ventas.`);
      
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
            <span className="text-xs font-bold text-muted uppercase">Efectivo Disponible en Caja:</span>
            <span className="font-bold text-lg color-primary">{formatBsS(availableCashBsS)}</span>
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

          <div className="form-group mb-3">
            <label className="form-label font-medium">1. Monto Entregado al Cliente (Efectivo en Bs.S - Solo Enteros) *</label>
            <input
              type="number"
              step="1"
              min="1"
              className="form-control form-control-lg font-bold"
              placeholder="0"
              value={amountBsS}
              onChange={(e) => setAmountBsS(e.target.value)}
              onKeyDown={handleKeyDown}
              required
              autoFocus
            />
            <small className="text-muted text-xs mt-1 block">
              Solo números enteros sin decimales (ej. 10, 50, 100).
            </small>
          </div>

          <div className="form-group mb-4">
            <label className="form-label font-medium">2. Método de Cobro al Cliente (Solo Electrónico) *</label>
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
              <span className="font-bold color-danger">-{formatBsS(numRequested)}</span>
            </div>

            <div className="flex-between text-sm mb-2">
              <span className="text-muted">Comisión de Ganancia {hasResolvedCommission ? `(${commissionPercentage}%)` : ''}:</span>
              <span className="font-bold color-success">{hasResolvedCommission ? `+${formatBsS(commissionBsS)}` : '—'}</span>
            </div>

            <div className="divider my-2"></div>

            <div className="flex-between flex-align-center">
              <span className="font-bold text-sm">TOTAL A COBRAR AL CLIENTE:</span>
              <div className="text-right">
                <div className="font-bold text-lg color-primary">{hasResolvedCommission ? formatBsS(totalChargedBsS) : '—'}</div>
                <div className="text-xs text-muted">{hasResolvedCommission ? `(${formatUSD(totalChargedUsd)})` : ''}</div>
              </div>
            </div>
          </div>

          <div className="modal-actions flex-center gap-3 pt-2 regmod-actions">
            <button type="button" className="btn btn-outline flex-center regmod-btn-130" onClick={handleClose} disabled={loading}>
              Cancelar
            </button>
            <button type="submit" className="btn btn-primary flex-center gap-2 font-bold regmod-btn-180" disabled={loading || numRequested <= 0 || !hasResolvedCommission}>
              {loading ? <Loader2 size={16} className="animate-spin" /> : <FastForward size={16} />}
              Procesar Adelanto
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
