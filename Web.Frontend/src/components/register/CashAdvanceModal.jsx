import { useState, useEffect } from 'react';
import Modal from '../ui/Modal';
import { FastForward, Loader2, AlertCircle, CheckCircle2 } from 'lucide-react';
import { api } from '../../services/api';
import { getActivePaymentMethods } from '../../services/paymentApi';
import { formatBsS, formatUSD } from '../../utils/formatters';

export default function CashAdvanceModal({ isOpen, onClose, sessionId, availableCashBsS, exchangeRate, user, onSuccess }) {
  const [amountBsS, setAmountBsS] = useState('');
  const [paymentMethods, setPaymentMethods] = useState([]);
  const [selectedMethodId, setSelectedMethodId] = useState('');
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

  const commissionPercentage = isTransfer ? 7 : 10;
  const numRequested = parseFloat(amountBsS) || 0;
  const commissionBsS = Math.round(numRequested * (commissionPercentage / 100));
  const totalChargedBsS = numRequested + commissionBsS;
  const totalChargedUsd = (exchangeRate && exchangeRate > 0) ? totalChargedBsS / exchangeRate : 0;

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
                  {m.name} ({m.name.toLowerCase().includes('transfer') || m.name.toLowerCase().includes('pago m') ? 'Comisión 7%' : 'Comisión 10%'})
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
              <span className="text-muted">Comisión de Ganancia ({commissionPercentage}%):</span>
              <span className="font-bold color-success">+{formatBsS(commissionBsS)}</span>
            </div>

            <div className="divider my-2"></div>

            <div className="flex-between flex-align-center">
              <span className="font-bold text-sm">TOTAL A COBRAR AL CLIENTE:</span>
              <div className="text-right">
                <div className="font-bold text-lg color-primary">{formatBsS(totalChargedBsS)}</div>
                <div className="text-xs text-muted">({formatUSD(totalChargedUsd)})</div>
              </div>
            </div>
          </div>

          <div className="modal-actions flex-center gap-3 pt-2" style={{ display: 'flex', justifyContent: 'center', alignItems: 'center' }}>
            <button type="button" className="btn btn-outline flex-center" onClick={handleClose} disabled={loading} style={{ minWidth: '130px', justifyContent: 'center' }}>
              Cancelar
            </button>
            <button type="submit" className="btn btn-primary flex-center gap-2 font-bold" disabled={loading || numRequested <= 0} style={{ minWidth: '180px', justifyContent: 'center' }}>
              {loading ? <Loader2 size={16} className="animate-spin" /> : <FastForward size={16} />}
              Procesar Adelanto
            </button>
          </div>
        </form>
      )}
    </Modal>
  );
}
