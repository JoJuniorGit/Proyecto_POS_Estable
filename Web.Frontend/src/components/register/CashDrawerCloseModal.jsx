import { useEffect, useRef, useState } from 'react';
import Modal from '../ui/Modal';
import AtmAmountInput from '../ui/AtmAmountInput';
import { Lock, Loader2, AlertCircle, ShieldAlert } from 'lucide-react';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode } from '../../utils/monetaryProfile';
import { closeCashDrawerSession, createIdempotencyKey } from '../../services/pettyCashApi';
import {
  buildCashDrawerClosePayload,
  canCloseCashDrawerSession,
  resolveLocalCurrencyProfile,
  validateCashDrawerCloseDraft,
} from './registerFormLogic';
import './RegisterModals.css';
import './PettyCashModals.css';

export const CASH_DRAWER_CLOSE_TITLE = 'Cierre de Caja';

/**
 * T5 (Paso 13 / 8.188, S2): arqueo y cierre de la sesión de caja con conteo USD opcional.
 * Paridad WPF: el conteo Bs.S es obligatorio, el USD es opcional y su ausencia conserva el
 * contrato previo del cierre (actualClosingBalanceUsd null). El cierre diario (Reporte Z) se
 * procesa por su propio flujo; acá solo se cierra la sesión/gaveta.
 */
export default function CashDrawerCloseModal({
  isOpen,
  onClose,
  exchangeRate,
  expectedCashBsS = 0,
  expectedCashUsd = null,
  role,
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
  const [usdAmount, setUsdAmount] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const idempotencyAttemptRef = useRef(null);

  const isAllowed = canCloseCashDrawerSession(role);
  const hasTheoreticalUsd = expectedCashUsd !== null && expectedCashUsd !== undefined;

  useEffect(() => {
    if (!isOpen) {
      idempotencyAttemptRef.current = null;
      return;
    }
    setLocalAmount(Number(expectedCashBsS) || 0);
    setUsdAmount('');
    setLoading(false);
    setError('');
    idempotencyAttemptRef.current = null;
  }, [isOpen, expectedCashBsS]);

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');

    if (!isAllowed) {
      setError('Solo un Administrador o Gerente puede cerrar la sesión de caja.');
      return;
    }

    const validation = validateCashDrawerCloseDraft({
      actualClosingBalanceLocal: localAmount,
      actualClosingBalanceUsd: usdAmount,
      localCurrencyLabel,
      usdCurrencyLabel,
    });
    if (validation) {
      setError(validation);
      return;
    }

    setLoading(true);
    try {
      const payload = buildCashDrawerClosePayload({
        actualClosingBalanceLocal: localAmount,
        actualClosingBalanceUsd: usdAmount,
        currentExchangeRate: exchangeRate,
      });
      const signature = JSON.stringify(payload);
      if (idempotencyAttemptRef.current?.signature !== signature) {
        idempotencyAttemptRef.current = { signature, key: createIdempotencyKey() };
      }
      const result = await closeCashDrawerSession(payload, {
        idempotencyKey: idempotencyAttemptRef.current.key,
      });
      idempotencyAttemptRef.current = null;
      onSuccess?.(result);
      onClose?.();
    } catch (err) {
      console.error('Error cerrando la sesión de caja:', err);
      setError(err?.message || 'No se pudo cerrar la sesión de caja.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={CASH_DRAWER_CLOSE_TITLE} maxWidth="520px">
      <form onSubmit={handleSubmit}>
        {error && (
          <div className="alert alert-danger mb-3 flex-align-center gap-2">
            <AlertCircle size={18} />
            <span>{error}</span>
          </div>
        )}

        {!isAllowed ? (
          <div className="alert alert-warning flex-align-center gap-2 text-sm">
            <ShieldAlert size={18} className="flex-shrink-0" />
            <span>Solo un Administrador o Gerente puede cerrar la sesión de caja.</span>
          </div>
        ) : (
          <>
            <div className="pcm-close-summary p-3 mb-3">
              <span className="pcm-close-summary-line">
                Efectivo esperado {localCurrencyLabel}: {formatSecondary(expectedCashBsS || 0)}
              </span>
              {isDual && (
                <span className="pcm-close-summary-line">
                  {hasTheoreticalUsd
                    ? `Efectivo esperado ${usdCurrencyLabel}: ${formatPrimary(expectedCashUsd)}`
                    : `Efectivo esperado ${usdCurrencyLabel}: no disponible`}
                </span>
              )}
              {isDual && (
                <span className="pcm-close-hint">
                  Tasa vigente: {formatSecondary(exchangeRate || 0)} / {primaryCurrencyLabel}
                </span>
              )}
            </div>

            <div className="form-group mb-3">
              <label className="form-label font-medium">Efectivo {localCurrencyLabel} contado *</label>
              <AtmAmountInput
                value={localAmount}
                onChange={(numeric) => setLocalAmount(numeric)}
                prefix={localCurrencySymbol}
                disabled={loading}
                autoFocus
              />
            </div>

            {isDual && (
              <div className="form-group mb-2">
                <label className="form-label font-medium">Efectivo {usdCurrencyLabel} contado (opcional)</label>
                <AtmAmountInput
                  value={usdAmount}
                  onChange={(numeric) => setUsdAmount(numeric)}
                  prefix={usdCurrencySymbol}
                  disabled={loading}
                />
              </div>
            )}

            <p className="text-muted text-xs mb-3">
              {isDual
                ? `El conteo ${usdCurrencyLabel} es opcional; si se deja vacío se conserva el comportamiento previo del cierre.`
                : `El conteo físico se registra únicamente en ${localCurrencyLabel}.`}
              {' '}El cierre diario (Reporte Z) se procesa desde la vista Cierre Diario.
            </p>

            <div className="modal-actions flex-center gap-3 pt-2 regmod-actions">
              <button type="button" className="btn btn-outline flex-center regmod-btn-130" onClick={onClose} disabled={loading}>
                Cancelar
              </button>
              <button type="submit" className="btn btn-primary flex-center gap-2 font-bold regmod-btn-180" disabled={loading}>
                {loading ? <Loader2 size={16} className="animate-spin" /> : <Lock size={16} />}
                CONFIRMAR CIERRE
              </button>
            </div>
          </>
        )}
      </form>
    </Modal>
  );
}
