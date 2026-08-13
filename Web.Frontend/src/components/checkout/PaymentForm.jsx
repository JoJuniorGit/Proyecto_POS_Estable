import { useState, useEffect } from 'react';
import { Plus } from 'lucide-react';
import { formatNumberEs } from '../../utils/formatters';

export default function PaymentForm({ methods, remainingBsS, exchangeRate, onAddPayment }) {
  const [selectedMethodId, setSelectedMethodId] = useState('');
  const [cents, setCents] = useState(0);
  const [isFreshFocus, setIsFreshFocus] = useState(true);
  const [reference, setReference] = useState('');

  // Seleccionar automáticamente el primer método y prellenar el monto con el saldo restante
  useEffect(() => {
    if (methods && methods.length > 0 && !selectedMethodId) {
      setSelectedMethodId(methods[0].id.toString());
    }
  }, [methods, selectedMethodId]);

  useEffect(() => {
    if (remainingBsS > 0) {
      setCents(Math.round(remainingBsS * 100));
    } else {
      setCents(0);
    }
    setIsFreshFocus(true);
  }, [remainingBsS]);

  const selectedMethod = methods.find((m) => m.id.toString() === selectedMethodId);
  const parsedAmount = cents / 100;
  const displayAmount = formatNumberEs(parsedAmount);
  const usdPreview = exchangeRate > 0 ? (parsedAmount / exchangeRate).toFixed(2) : '0.00';

  const handleKeyDown = (e) => {
    // Permitir teclas de navegación sin modificar estado
    if (['Tab', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Enter'].includes(e.key)) {
      return;
    }

    // Bloquear explícitamente caracteres no numéricos o símbolos inválidos
    if (['e', 'E', '+', '-', '.', ','].includes(e.key)) {
      e.preventDefault();
      return;
    }

    if (e.key === 'Backspace') {
      e.preventDefault();
      if (isFreshFocus) {
        setCents(0);
        setIsFreshFocus(false);
      } else {
        setCents((prev) => Math.floor(prev / 10));
      }
      return;
    }

    if (e.key === 'Delete') {
      e.preventDefault();
      setCents(0);
      setIsFreshFocus(false);
      return;
    }

    if (/^[0-9]$/.test(e.key)) {
      e.preventDefault();
      const digit = parseInt(e.key, 10);
      if (isFreshFocus) {
        setCents(digit);
        setIsFreshFocus(false);
      } else {
        setCents((prev) => {
          const next = prev * 10 + digit;
          return next > 999999999 ? prev : next;
        });
      }
      return;
    }

    // Prevenir cualquier otro caracter tipiado que no sean modificadores de sistema (Ctrl/Cmd)
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
    }
  };

  const handlePaste = (e) => {
    e.preventDefault();
    const pastedData = e.clipboardData.getData('text');
    const rawDigits = pastedData.replace(/\D/g, '');
    if (rawDigits) {
      const parsedCents = parseInt(rawDigits, 10);
      setCents(parsedCents > 999999999 ? 999999999 : parsedCents);
      setIsFreshFocus(false);
    }
  };

  const handleFocus = (e) => {
    setIsFreshFocus(true);
    e.target.select();
  };

  const handleSubmit = (e) => {
    e.preventDefault();
    if (!selectedMethod || parsedAmount <= 0) return;

    if (selectedMethod.requiresReference && !reference.trim()) {
      alert(`El método de pago (${selectedMethod.name}) requiere un número de referencia.`);
      return;
    }

    onAddPayment({
      methodId: selectedMethod.id,
      methodName: selectedMethod.name,
      isCash: selectedMethod.isCash,
      amountBsS: parsedAmount,
      amountUsd: parseFloat(usdPreview),
      reference: reference.trim() || null,
    });

    setReference('');
  };

  return (
    <form className="payment-form" onSubmit={handleSubmit}>
      <div className="form-group">
        <label className="form-label">Método de Pago</label>
        <select
          className="form-select"
          value={selectedMethodId}
          onChange={(e) => setSelectedMethodId(e.target.value)}
        >
          {methods.map((method) => (
            <option key={method.id} value={method.id}>
              {method.name} {method.isCash ? '(Efectivo)' : ''}
            </option>
          ))}
        </select>
      </div>

      <div className="form-row">
        <div className="form-group flex-1">
          <label className="form-label">Monto (Bs.S)</label>
          <input
            type="text"
            inputMode="numeric"
            className="form-input font-bold"
            value={displayAmount}
            onKeyDown={handleKeyDown}
            onPaste={handlePaste}
            onFocus={handleFocus}
            onChange={() => {}}
          />
        </div>

        <div className="form-group flex-1">
          <label className="form-label">Equivalente USD</label>
          <div className="form-read-only">$ {formatNumberEs(parseFloat(usdPreview))} USD</div>
        </div>
      </div>

      {selectedMethod?.requiresReference && (
        <div className="form-group">
          <label className="form-label">Número de Referencia *</label>
          <input
            type="text"
            className="form-input"
            placeholder="Ingrese el N° de transacción / referencia"
            value={reference}
            onChange={(e) => setReference(e.target.value)}
            required
          />
        </div>
      )}

      <button
        type="submit"
        className="btn btn-outline btn-block"
        disabled={parsedAmount <= 0}
      >
        <Plus size={16} /> Agregar Pago
      </button>
    </form>
  );
}
