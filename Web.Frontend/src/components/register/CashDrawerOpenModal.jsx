import { useEffect, useState } from 'react';
import Modal from '../ui/Modal';
import AtmAmountInput from '../ui/AtmAmountInput';
import { Landmark, Loader2, AlertCircle } from 'lucide-react';
import { useCurrencyFormat } from '../../context/CurrencyFormatContext';
import { openCashDrawerSession } from '../../services/pettyCashApi';
import {
  buildCashDrawerOpenPayload,
  validateCashDrawerOpenDraft,
} from './registerFormLogic';
import './RegisterModals.css';
import './PettyCashModals.css';

export const CASH_DRAWER_OPEN_TITLE = 'Apertura de Caja';

/**
 * 8.201 (S1): apertura manual de la sesión con fondo inicial segregado por moneda. El backend
 * persiste `openingBalanceUsd` y el rollover lo transfiere al turno siguiente. El auto-alta
 * existente (al primer cobro) no cambia: este modal es solo para la apertura explícita.
 */
export default function CashDrawerOpenModal({
  isOpen,
  onClose,
  exchangeRate,
  onSuccess,
}) {
  const { formatBsS, formatUSD } = useCurrencyFormat();
  const [localAmount, setLocalAmount] = useState(0);
  const [usdAmount, setUsdAmount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!isOpen) return;
    setLocalAmount(0);
    setUsdAmount(0);
    setLoading(false);
    setError('');
  }, [isOpen]);

  const hasRate = Number(exchangeRate) > 0;

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');

    const validation = validateCashDrawerOpenDraft({
      openingBalanceLocal: localAmount,
      openingBalanceUsd: usdAmount,
      currentExchangeRate: exchangeRate,
    });
    if (validation) {
      setError(validation);
      return;
    }

    setLoading(true);
    try {
      const result = await openCashDrawerSession(buildCashDrawerOpenPayload({
        openingBalanceLocal: localAmount,
        openingBalanceUsd: usdAmount,
        currentExchangeRate: exchangeRate,
      }));
      onSuccess?.(result);
      onClose?.();
    } catch (err) {
      console.error('Error abriendo la sesión de caja:', err);
      setError(err?.message || 'No se pudo abrir la sesión de caja.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={CASH_DRAWER_OPEN_TITLE} maxWidth="520px">
      <form onSubmit={handleSubmit}>
        {error && (
          <div className="alert alert-danger mb-3 flex-align-center gap-2">
            <AlertCircle size={18} />
            <span>{error}</span>
          </div>
        )}

        <div className="alert alert-info mb-3 flex-align-center gap-2 text-sm">
          <Landmark size={18} className="flex-shrink-0" />
          <span>
            Declare el fondo físico de cada gaveta. El fondo USD queda segregado y se transfiere
            automáticamente al siguiente turno.
          </span>
        </div>

        <div className="grid grid-2 gap-3 mb-3">
          <div className="form-group">
            <label className="form-label font-medium" htmlFor="cash-drawer-open-local">
              Fondo inicial Bs.S *
            </label>
            <AtmAmountInput
              id="cash-drawer-open-local"
              value={localAmount}
              onChange={(numeric) => setLocalAmount(numeric)}
              prefix="Bs.S"
              disabled={loading}
              autoFocus
            />
          </div>

          <div className="form-group">
            <label className="form-label font-medium" htmlFor="cash-drawer-open-usd">
              Fondo inicial USD
            </label>
            <AtmAmountInput
              id="cash-drawer-open-usd"
              value={usdAmount}
              onChange={(numeric) => setUsdAmount(numeric)}
              prefix="$"
              disabled={loading}
            />
          </div>
        </div>

        <p className="text-xs text-muted mb-3">
          Tasa aplicada: {hasRate ? `${formatBsS(exchangeRate)} / USD` : 'sin tasa configurada'}.
          {hasRate && ` Equivalente USD del fondo local: ${formatUSD(Number(localAmount) / Number(exchangeRate))}.`}
        </p>

        <div className="flex-align-center justify-content-end gap-2">
          <button type="button" className="btn btn-outline" onClick={onClose} disabled={loading}>
            Cancelar
          </button>
          <button type="submit" className="btn btn-primary flex-align-center gap-2" disabled={loading || !hasRate}>
            {loading ? <Loader2 size={16} className="animate-spin" /> : <Landmark size={16} />}
            Abrir Caja
          </button>
        </div>
      </form>
    </Modal>
  );
}
