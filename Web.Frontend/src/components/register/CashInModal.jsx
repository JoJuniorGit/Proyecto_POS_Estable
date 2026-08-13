import { useState } from 'react';
import Modal from '../ui/Modal';
import { ArrowDownLeft, Loader2, AlertCircle } from 'lucide-react';
import { api } from '../../services/api';

export default function CashInModal({ isOpen, onClose, sessionId, exchangeRate, user, onSuccess }) {
  const [amountBsS, setAmountBsS] = useState('');
  const [reason, setReason] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleClose = () => {
    setAmountBsS('');
    setReason('');
    setError('');
    onClose();
  };

  const handleKeyDown = (e) => {
    if (e.key === '.' || e.key === ',' || e.key === 'e' || e.key === 'E' || e.key === '+' || e.key === '-') {
      e.preventDefault();
    }
  };

  const handleSubmit = async (e) => {
    e.preventDefault();
    setError('');

    if (!sessionId) {
      setError('No se detectó una sesión de caja activa. Por favor actualice la página.');
      return;
    }

    const numAmount = parseFloat(amountBsS);
    if (isNaN(numAmount) || numAmount <= 0) {
      setError('Ingrese un monto válido mayor a cero.');
      return;
    }

    if (!Number.isInteger(numAmount) || numAmount % 1 !== 0) {
      setError('El monto de efectivo debe ser un número entero estrictamente sin decimales.');
      return;
    }

    const cleanReason = (reason.trim() || 'Ingreso de Caja').slice(0, 40).trim();
    const userName = user?.name || user?.fullName || 'Admin';
    const formattedDescription = `${cleanReason} - ${userName}`;

    setLoading(true);
    try {
      await api.post('/api/cashdrawer/transaction', {
        sessionId,
        amountLocal: numAmount,
        type: 0, // Income
        source: 5, // CashIn
        description: formattedDescription,
        exchangeRate: exchangeRate || 1
      });

      handleClose();
      if (onSuccess) onSuccess();
    } catch (err) {
      console.error('Error en CASH IN:', err);
      setError(typeof err === 'string' ? err : err.message || 'Error al procesar el ingreso de efectivo.');
    } finally {
      setLoading(false);
    }
  };

  const charCount = reason.length;

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="CASH IN (Ingresar Efectivo a Caja)" maxWidth="480px">
      <form onSubmit={handleSubmit} className="cash-in-form">
        {error && (
          <div className="alert alert-danger mb-3 flex-align-center gap-2">
            <AlertCircle size={18} />
            <span>{error}</span>
          </div>
        )}

        <div className="form-group mb-3">
          <label className="form-label font-medium">1. Monto a Ingresar (Bs.S - Solo Enteros) *</label>
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
          <div className="flex-between mb-1">
            <label className="form-label font-medium mb-0">2. Concepto / Descripción *</label>
            <span className={`text-xs ${charCount >= 40 ? 'color-danger font-bold' : 'text-muted'}`}>
              {charCount}/40 caracteres
            </span>
          </div>
          <input
            type="text"
            className="form-control"
            placeholder="Ej. Reposición de cambio"
            maxLength={40}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            required
          />
          <small className="text-muted text-xs mt-1 block">
            Se registrará como: "{ (reason.trim() || 'Ingreso de Caja').slice(0, 40) } - { user?.name || 'Admin' }"
          </small>
        </div>

        <div className="modal-actions flex-center gap-3 pt-2" style={{ display: 'flex', justifyContent: 'center', alignItems: 'center' }}>
          <button type="button" className="btn btn-outline flex-center" onClick={handleClose} disabled={loading} style={{ minWidth: '130px', justifyContent: 'center' }}>
            Cancelar
          </button>
          <button type="submit" className="btn btn-success flex-center gap-2 font-bold" disabled={loading} style={{ minWidth: '180px', justifyContent: 'center' }}>
            {loading ? <Loader2 size={16} className="animate-spin" /> : <ArrowDownLeft size={16} />}
            Confirmar CASH IN
          </button>
        </div>
      </form>
    </Modal>
  );
}
