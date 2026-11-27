import { useEffect, useRef, useState } from 'react';
import Modal from '../ui/Modal';
import AtmAmountInput from '../ui/AtmAmountInput';
import { Landmark, Loader2, AlertCircle } from 'lucide-react';
import { useMonetaryProfile } from '../../context/MonetaryProfileContext';
import { displayCurrencyCode, normalizeCurrencyCode } from '../../utils/monetaryProfile';
import { createIdempotencyKey, openCashDrawerSession } from '../../services/pettyCashApi';
import { getActivePaymentMethods } from '../../services/paymentApi';
import {
  buildCashDrawerOpenMethodBreakdown,
  buildCashDrawerOpenPayload,
  profileIncludesCurrency,
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
  const [physicalMethods, setPhysicalMethods] = useState([]);
  const [methodAmounts, setMethodAmounts] = useState({});
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const idempotencyAttemptRef = useRef(null);

  useEffect(() => {
    if (!isOpen) {
      idempotencyAttemptRef.current = null;
      return undefined;
    }
    setLocalAmount(0);
    setUsdAmount(0);
    setLoading(false);
    setError('');
    idempotencyAttemptRef.current = null;

    let cancelled = false;
    getActivePaymentMethods()
      .then((methods) => {
        if (cancelled) return;
        const rows = (methods || []).filter((method) =>
          method.isCash
          && method.isActive !== false
          && method.isDeleted !== true
          && (!method.currency || profileIncludesCurrency(profile, method.currency)));
        setPhysicalMethods(rows);
        setMethodAmounts(Object.fromEntries(rows.map((method) => [method.id, 0])));
      })
      .catch((err) => {
        if (!cancelled) console.warn('[CashDrawerOpen] No se pudieron cargar los métodos físicos:', err);
      });

    return () => {
      cancelled = true;
    };
  }, [isOpen, profile]);

  const hasPhysicalMethods = physicalMethods.length > 0;
  const hasRate = Number(exchangeRate) > 0;
  const displayedLocalAmount = hasPhysicalMethods
    ? physicalMethods.reduce((sum, method) => (
      methodCurrencyCode(method) === localCurrencyCode
        ? sum + (Number(methodAmounts[method.id]) || 0)
        : sum
    ), 0)
    : Number(localAmount) || 0;

  function methodCurrencyCode(method) {
    return normalizeCurrencyCode(method?.currency) || localCurrencyCode;
  }

  function methodLabel(method) {
    return displayCurrencyCode(methodCurrencyCode(method));
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');

    let openingLocal = Number(localAmount) || 0;
    let openingUsd = Number(usdAmount) || 0;
    let methodBalances = null;

    if (hasPhysicalMethods) {
      const breakdown = buildCashDrawerOpenMethodBreakdown({
        methods: physicalMethods,
        amounts: methodAmounts,
        localCurrencyCode,
      });
      if (breakdown.error) {
        setError(breakdown.error);
        return;
      }

      openingLocal = breakdown.openingBalanceLocal;
      openingUsd = breakdown.openingBalanceUsd;
      methodBalances = breakdown.methodBalances;
    }

    const validation = validateCashDrawerOpenDraft({
      openingBalanceLocal: openingLocal,
      openingBalanceUsd: openingUsd,
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
        openingBalanceLocal: openingLocal,
        openingBalanceUsd: openingUsd,
        currentExchangeRate: exchangeRate,
      });
      if (methodBalances?.length) {
        payload.methodBalances = methodBalances;
      }
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

        {hasPhysicalMethods ? (
          <div className="grid grid-1 gap-3 mb-3">
            {physicalMethods.map((method, index) => (
              <div className="form-group" key={method.id}>
                <label className="form-label font-medium" htmlFor={`cash-drawer-open-method-${method.id}`}>
                  Fondo inicial {methodLabel(method)} — {method.name} *
                </label>
                <AtmAmountInput
                  id={`cash-drawer-open-method-${method.id}`}
                  value={methodAmounts[method.id] ?? 0}
                  onChange={(numeric) => setMethodAmounts((current) => ({ ...current, [method.id]: numeric }))}
                  prefix={symbolFor(methodCurrencyCode(method)) || methodLabel(method)}
                  disabled={loading}
                  autoFocus={index === 0}
                />
              </div>
            ))}
          </div>
        ) : (
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
        )}

        {isDual && (
          <p className="text-xs text-muted mb-3">
            Tasa aplicada: {hasRate ? `${formatSecondary(exchangeRate)} / ${primaryCurrencyLabel}` : 'sin tasa configurada'}.
            {hasRate && ` Equivalente ${primaryCurrencyLabel} del fondo local: ${formatPrimary(displayedLocalAmount / Number(exchangeRate))}.`}
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
