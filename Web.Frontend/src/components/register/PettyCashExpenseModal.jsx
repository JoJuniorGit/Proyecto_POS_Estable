import { useEffect, useRef, useState } from 'react';
import Modal from '../ui/Modal';
import AtmAmountInput from '../ui/AtmAmountInput';
import { HandCoins, Loader2, AlertCircle, ShieldAlert } from 'lucide-react';
import { useCurrencyFormat } from '../../context/CurrencyFormatContext';
import { createCheckoutKeyHolder } from '../../utils/idempotency';
import {
  PETTY_CASH_CATEGORY_OPTIONS,
  PETTY_CASH_CURRENCY_OPTIONS,
  PettyCashCurrency,
  buildPettyCashAuthorizationMessage,
  isPettyCashAuthorizationError,
  registerPettyCashExpense,
} from '../../services/pettyCashApi';
import {
  computePettyCashEquivalent,
  validatePettyCashDraft,
} from './registerFormLogic';
import './RegisterModals.css';
import './PettyCashModals.css';

/**
 * T5 (Paso 13 / 8.188, S1/S4): registro de un egreso de caja chica. El operador elige la divisa
 * física, captura el nominal y ve la equivalencia dual a la tasa vigente; el servidor resuelve la
 * tasa BCV, el snapshot bimonetario y la liquidez. Ante el 403 de autorización se muestra el
 * contrato (autorización presencial/supervisora desde una caja WPF en esta fase) sin reintentar
 * a ciegas: la clave de idempotencia queda estable por si el operador reintenta el mismo intento.
 */
export default function PettyCashExpenseModal({
  isOpen,
  onClose,
  sessionId,
  exchangeRate,
  role,
  onSuccess,
}) {
  const { formatBsS, formatUSD } = useCurrencyFormat();
  const [currency, setCurrency] = useState(PettyCashCurrency.BsS);
  const [amount, setAmount] = useState(0);
  const [category, setCategory] = useState('');
  const [concept, setConcept] = useState('');
  const [beneficiary, setBeneficiary] = useState('');
  const [referenceNumber, setReferenceNumber] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [authorizationMessage, setAuthorizationMessage] = useState('');

  // La clave es estable mientras dura el intento (reintento de red o del operador comparte la
  // misma clave) y se resetea al abrir/cerrar el modal para la próxima operación.
  const cashKeyHolderRef = useRef(null);
  if (!cashKeyHolderRef.current) {
    cashKeyHolderRef.current = createCheckoutKeyHolder();
  }

  useEffect(() => {
    if (!isOpen) return;
    cashKeyHolderRef.current.reset();
    setCurrency(PettyCashCurrency.BsS);
    setAmount(0);
    setCategory('');
    setConcept('');
    setBeneficiary('');
    setReferenceNumber('');
    setLoading(false);
    setError('');
    setAuthorizationMessage('');
  }, [isOpen]);

  const isUsd = currency === PettyCashCurrency.Usd;
  const hasRate = Number(exchangeRate) > 0;
  const equivalent = computePettyCashEquivalent({ currency, amount, exchangeRate });

  const equivalentDisplay = !hasRate
    ? 'Configure la tasa de cambio vigente.'
    : (isUsd ? `≈ ${formatBsS(equivalent.bsS)}` : `≈ ${formatUSD(equivalent.usd)}`);

  function handleClose() {
    cashKeyHolderRef.current.reset();
    onClose?.();
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setError('');
    setAuthorizationMessage('');

    if (!sessionId) {
      setError('No se detectó una sesión de caja activa. Por favor actualice la página.');
      return;
    }

    const validation = validatePettyCashDraft({
      exchangeRate,
      amount,
      category,
      concept,
      beneficiary,
      referenceNumber,
    });
    if (validation) {
      setError(validation);
      return;
    }

    setLoading(true);
    try {
      const result = await registerPettyCashExpense({
        currency,
        amount,
        category: Number(category),
        concept,
        beneficiary,
        referenceNumber,
      }, { idempotencyKey: cashKeyHolderRef.current.getOrCreateKey() });

      cashKeyHolderRef.current.reset();
      onSuccess?.(result);
      handleClose();
    } catch (err) {
      if (isPettyCashAuthorizationError(err)) {
        setAuthorizationMessage(buildPettyCashAuthorizationMessage(err, { formatBsS, formatUSD }));
      } else {
        console.error('Error registrando el gasto de caja chica:', err);
        setError(err?.message || 'No se pudo registrar el gasto de caja chica.');
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title="Registrar Gasto de Caja Chica" maxWidth="560px">
      <form onSubmit={handleSubmit} className="petty-cash-form">
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

        {role === 'Cashier' && (
          <div className="alert alert-info mb-3 flex-align-center gap-2 text-sm">
            <ShieldAlert size={18} className="flex-shrink-0" />
            <span>
              El registro de caja chica iniciado por un cajero requiere autorización de un Administrador
              o Supervisor. En esta fase, la autorización debe realizarse de forma presencial desde una
              caja WPF.
            </span>
          </div>
        )}

        <div className="grid grid-2 gap-3 mb-3">
          <div className="form-group">
            <label className="form-label font-medium" htmlFor="petty-cash-currency">Moneda de extracción física *</label>
            <select
              id="petty-cash-currency"
              className="form-select"
              value={currency}
              onChange={(e) => setCurrency(Number(e.target.value))}
              disabled={loading}
            >
              {PETTY_CASH_CURRENCY_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>{option.label}</option>
              ))}
            </select>
          </div>

          <div className="form-group">
            <label className="form-label font-medium" htmlFor="petty-cash-amount">
              {isUsd ? 'Monto (USD)' : 'Monto (Bs.S)'} *
            </label>
            <AtmAmountInput
              value={amount}
              onChange={(numeric) => setAmount(numeric)}
              prefix={isUsd ? '$' : 'Bs.S'}
              disabled={loading}
              autoFocus
            />
          </div>
        </div>

        <div className="form-group mb-3">
          <label className="form-label font-medium" htmlFor="petty-cash-category">Categoría operativa *</label>
          <select
            id="petty-cash-category"
            className="form-select"
            value={category}
            onChange={(e) => setCategory(e.target.value === '' ? '' : Number(e.target.value))}
            disabled={loading}
          >
            <option value="">Seleccione la categoría...</option>
            {PETTY_CASH_CATEGORY_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </select>
        </div>

        <div className="form-group mb-3">
          <label className="form-label font-medium" htmlFor="petty-cash-concept">Concepto del gasto *</label>
          <input
            id="petty-cash-concept"
            type="text"
            className="form-control"
            placeholder="Ej. Compra de jabón y desinfectante para el área de caja"
            maxLength={500}
            value={concept}
            onChange={(e) => setConcept(e.target.value)}
            disabled={loading}
          />
          <small className="text-muted text-xs mt-1 block">{concept.length}/500 caracteres</small>
        </div>

        <div className="grid grid-2 gap-3 mb-3">
          <div className="form-group">
            <label className="form-label font-medium" htmlFor="petty-cash-beneficiary">Beneficiario o proveedor *</label>
            <input
              id="petty-cash-beneficiary"
              type="text"
              className="form-control"
              placeholder="Ej. Limpieza Total C.A."
              maxLength={150}
              value={beneficiary}
              onChange={(e) => setBeneficiary(e.target.value)}
              disabled={loading}
            />
            <small className="text-muted text-xs mt-1 block">{beneficiary.length}/150 caracteres</small>
          </div>

          <div className="form-group">
            <label className="form-label font-medium" htmlFor="petty-cash-reference">Referencia del comprobante *</label>
            <input
              id="petty-cash-reference"
              type="text"
              className="form-control"
              placeholder="Ej. FAC-00123"
              maxLength={100}
              value={referenceNumber}
              onChange={(e) => setReferenceNumber(e.target.value)}
              disabled={loading}
            />
            <small className="text-muted text-xs mt-1 block">{referenceNumber.length}/100 caracteres</small>
          </div>
        </div>

        <div className="pcm-equivalence flex-between flex-align-center p-2 mb-3">
          <span className="text-xs text-muted">
            Tasa vigente: {formatBsS(exchangeRate || 0)} / USD
          </span>
          <span className="pcm-equivalence-value font-bold">{equivalentDisplay}</span>
        </div>

        <p className="text-muted text-xs mb-3">
          La equivalencia es informativa a la tasa vigente; el servidor fija el snapshot bimonetario
          definitivo y valida la liquidez de la gaveta al confirmar.
        </p>

        <div className="modal-actions flex-center gap-3 pt-2 regmod-actions">
          <button type="button" className="btn btn-outline flex-center regmod-btn-130" onClick={handleClose} disabled={loading}>
            Cancelar
          </button>
          <button type="submit" className="btn btn-primary flex-center gap-2 font-bold regmod-btn-180" disabled={loading}>
            {loading ? <Loader2 size={16} className="animate-spin" /> : <HandCoins size={16} />}
            Confirmar Gasto
          </button>
        </div>
      </form>
    </Modal>
  );
}
