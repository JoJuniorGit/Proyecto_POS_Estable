import { useEffect, useState } from 'react';
import Modal from '../ui/Modal';
import { CircleX, Loader2, AlertCircle, ShieldAlert } from 'lucide-react';
import { useCurrencyFormat } from '../../context/CurrencyFormatContext';
import {
  buildPettyCashAuthorizationMessage,
  cancelPettyCashExpense,
  getPettyCashCategoryLabel,
  isPettyCashAuthorizationError,
} from '../../services/pettyCashApi';
import {
  getPettyCashVoucherCode,
  requiresPettyCashCancellationAuthorization,
  validatePettyCashCancelReason,
} from './registerFormLogic';
import './RegisterModals.css';
import './PettyCashModals.css';

/**
 * T5 (Paso 13 / 8.188, S5): anulación lógica de un gasto de caja chica.
 * Solo Admin anula directo con motivo obligatorio (el servidor genera la reversa compensatoria
 * con los snapshots congelados). Manager y Cashier requieren autorización supervisora y esta
 * terminal web no produce el token en esta fase: se informa el gate presencial (caja WPF) en vez
 * de reintentar a ciegas.
 */
export default function PettyCashCancelModal({
  isOpen,
  onClose,
  expense,
  role,
  onSuccess,
}) {
  const { formatBsS, formatUSD } = useCurrencyFormat();
  const [reason, setReason] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [authorizationMessage, setAuthorizationMessage] = useState('');

  const requiresSupervisor = requiresPettyCashCancellationAuthorization(role);

  useEffect(() => {
    if (!isOpen) return;
    setReason('');
    setLoading(false);
    setError('');
    setAuthorizationMessage('');
  }, [isOpen]);

  const voucherCode = getPettyCashVoucherCode(expense || {});
  const expenseSummary = expense
    ? `${getPettyCashCategoryLabel(expense.category)} · ${formatBsS(expense.amountBsS)} / ${formatUSD(expense.amountUsd)}`
    : '';

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');
    setAuthorizationMessage('');

    if (!expense?.id) {
      setError('No se detectó el gasto a anular. Actualice el listado del turno.');
      return;
    }

    const validation = validatePettyCashCancelReason(reason);
    if (validation) {
      setError(validation);
      return;
    }

    setLoading(true);
    try {
      const result = await cancelPettyCashExpense(expense.id, reason);
      onSuccess?.(result);
      onClose?.();
    } catch (err) {
      if (isPettyCashAuthorizationError(err)) {
        setAuthorizationMessage(buildPettyCashAuthorizationMessage(err, { formatBsS, formatUSD }));
      } else {
        console.error('Error anulando el gasto de caja chica:', err);
        setError(err?.message || 'No se pudo anular el gasto de caja chica.');
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Anular Gasto de Caja Chica" maxWidth="520px">
      <form onSubmit={handleSubmit}>
        {error && (
          <div className="alert alert-danger mb-3 flex-align-center gap-2">
            <AlertCircle size={18} />
            <span>{error}</span>
          </div>
        )}

        {authorizationMessage && (
          <div className="alert alert-warning mb-3 flex-align-center gap-2">
            <ShieldAlert size={18} className="flex-shrink-0" />
            <span>{authorizationMessage}</span>
          </div>
        )}

        <div className="pcm-close-summary p-3 mb-3">
          <span className="pcm-close-summary-line font-mono">{voucherCode}</span>
          <span className="pcm-close-summary-line">{expense?.concept || '—'}</span>
          <span className="pcm-close-hint">{expense?.beneficiary || '—'}</span>
          <span className="pcm-close-hint">{expenseSummary}</span>
        </div>

        {requiresSupervisor ? (
          <div className="alert alert-warning flex-align-center gap-2 text-sm">
            <ShieldAlert size={18} className="flex-shrink-0" />
            <span>
              La anulación de un gasto de caja chica requiere autorización de un Administrador y, en
              esta fase, debe realizarse de forma presencial desde una caja WPF; esta terminal web no
              puede generar el token de autorización.
            </span>
          </div>
        ) : (
          <>
            <div className="form-group mb-3">
              <label className="form-label font-medium" htmlFor="petty-cash-cancel-reason">
                Motivo de la anulación *
              </label>
              <textarea
                id="petty-cash-cancel-reason"
                className="form-control"
                rows={3}
                maxLength={500}
                placeholder="Ej. Comprobante duplicado; el gasto se registró dos veces."
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                disabled={loading}
              />
              <small className="text-muted text-xs mt-1 block">{reason.length}/500 caracteres</small>
            </div>

            <p className="text-muted text-xs mb-3">
              La anulación conserva el registro histórico y genera una reversa compensatoria con los
              montos y la tasa originales (efecto neto 0 en la gaveta).
            </p>

            <div className="modal-actions flex-center gap-3 pt-2 regmod-actions">
              <button type="button" className="btn btn-outline flex-center regmod-btn-130" onClick={onClose} disabled={loading}>
                Cancelar
              </button>
              <button type="submit" className="btn btn-danger flex-center gap-2 font-bold regmod-btn-180" disabled={loading}>
                {loading ? <Loader2 size={16} className="animate-spin" /> : <CircleX size={16} />}
                CONFIRMAR ANULACIÓN
              </button>
            </div>
          </>
        )}
      </form>
    </Modal>
  );
}
