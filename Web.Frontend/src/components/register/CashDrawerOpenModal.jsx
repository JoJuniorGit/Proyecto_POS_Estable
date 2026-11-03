import { useEffect, useRef, useState } from 'react';
import Modal from '../ui/Modal';
import AtmAmountInput from '../ui/AtmAmountInput';
import { Landmark, Loader2, AlertCircle } from 'lucide-react';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { createIdempotencyKey, openCashDrawerSession } from '../../services/pettyCashApi';
import {
  buildCashDrawerOpenPayload,
  resolveLocalCurrencyProfile,
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
  const {
    profile,
    primaryCurrencyCode,
    formatPrimary,
    formatSecondary,
    symbolFor,
  } = useMonetaryProfile();
  const { isDual, code: localCurrencyCode, label: localCurrencyLabel } = resolveLocalCurrencyProfile(profile);
  const localCurrencySymbol = symbolFor(localCurrencyCode) || localCurrencyLabel;
  const primaryCurrencyLabel = displayCurrencyCode(primaryCurrencyCode);
  const usdCurrencyLabel = displayCurrencyCode('USD');
  const usdCurrencySymbol = symbolFor('USD') || usdCurrencyLabel;

  const [localAmount, setLocalAmount] = useState(0);
  const [usdAmount, setUsdAmount] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const idempotencyAttemptRef = useRef(null);

  useEffect(() => {
    if (!isOpen) {
      idempotencyAttemptRef.current = null;
      return;
    }
    setLocalAmount(0);
    setUsdAmount(0);
    setLoading(false);
    setError('');
    idempotencyAttemptRef.current = null;
  }, [isOpen]);

  const hasRate = Number(exchangeRate) > 0;

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');

    const validation = validateCashDrawerOpenDraft({
      openingBalanceLocal: localAmount,
      openingBalanceUsd: usdAmount,
      currentExchangeRate: exchangeRate,
      localCurrencyLabel,
      usdCurrencyLabel,
    });
    if (validation) {
      setError(validation);
      return;
    }

    setLoading(true);
    try {
      const payload = buildCashDrawerOpenPayload({
        openingBalanceLocal: localAmount,
        openingBalanceUsd: usdAmount,
        currentExchangeRate: exchangeRate,
      });
      const signature = JSON.stringify(payload);
      if (idempotencyAttemptRef.current?.signature !== signature) {
        idempotencyAttemptRef.current = { signature, key: createIdempotencyKey() };
      }
      const result = await openCashDrawerSession(payload, {
        idempotencyKey: idempotencyAttemptRef.current.key,
      });
      idempotencyAttemptRef.current = null;
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
            {isDual
              ? `Declare el fondo físico de la gaveta ${localCurrencyLabel} y de la gaveta ${usdCurrencyLabel}. El fondo ${usdCurrencyLabel} queda segregado y se transfiere automáticamente al siguiente turno.`
              : 'Declare el fondo físico de la gaveta local. El fondo se transfiere automáticamente al siguiente turno.'}
          </span>
        </div>

        <div className={`grid ${isDual ? 'grid-2' : 'grid-1'} gap-3 mb-3`}>
          <div className="form-group">
            <label className="form-label font-medium" htmlFor="cash-drawer-open-local">
              Fondo inicial {localCurrencyLabel} *
            </label>
            <AtmAmountInput
              id="cash-drawer-open-local"
              value={localAmount}
              onChange={(numeric) => setLocalAmount(numeric)}
              prefix={localCurrencySymbol}
              disabled={loading}
              autoFocus
            />
          </div>

          {isDual && (
            <div className="form-group">
              <label className="form-label font-medium" htmlFor="cash-drawer-open-usd">
                Fondo inicial {usdCurrencyLabel}
              </label>
              <AtmAmountInput
                id="cash-drawer-open-usd"
                value={usdAmount}
                onChange={(numeric) => setUsdAmount(numeric)}
                prefix={usdCurrencySymbol}
                disabled={loading}
              />
            </div>
          )}
        </div>

        {isDual && (
          <p className="text-xs text-muted mb-3">
            Tasa aplicada: {hasRate ? `${formatSecondary(exchangeRate)} / ${primaryCurrencyLabel}` : 'sin tasa configurada'}.
            {hasRate && ` Equivalente ${primaryCurrencyLabel} del fondo local: ${formatPrimary(Number(localAmount) / Number(exchangeRate))}.`}
          </p>
        )}

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
