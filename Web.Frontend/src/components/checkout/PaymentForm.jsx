import { useState, useEffect, useRef } from 'react';
import { Plus } from 'lucide-react';
import { formatNumberEs } from '../../utils/formatters';

export default function PaymentForm({ methods, remainingBsS, exchangeRate, onAddPayment }) {
  const [selectedMethodId, setSelectedMethodId] = useState('');
  const [amountText, setAmountText] = useState('');
  const [reference, setReference] = useState('');
  const inputRef = useRef(null);

  // Seleccionar automáticamente el primer método activo
  useEffect(() => {
    if (methods && methods.length > 0 && !selectedMethodId) {
      setSelectedMethodId(methods[0].id.toString());
    }
  }, [methods, selectedMethodId]);

  const selectedMethod = methods.find((m) => m.id.toString() === selectedMethodId);
  const isCashSelected = !!selectedMethod?.isCash;

  // Prellenar el monto al cambiar saldo restante o método de pago
  useEffect(() => {
    if (remainingBsS > 0) {
      setAmountText(isCashSelected ? Math.round(remainingBsS).toString() : remainingBsS.toFixed(2));
    } else {
      setAmountText('');
    }
  }, [remainingBsS, isCashSelected]);

  // Normalización del texto (soporta punto y coma decimal)
  const normalizedText = amountText.replace(',', '.').trim();
  const parsedAmount = parseFloat(normalizedText);
  const isValidNum = !isNaN(parsedAmount) && parsedAmount > 0 && isFinite(parsedAmount);

  // Verificación de número entero o terminación en .00 (ej: 10, 12.00, 15.00)
  const isIntegerOrZeroDecimal = isValidNum && Math.abs(parsedAmount - Math.round(parsedAmount)) < 0.0001;

  // Validación de monto:
  // - Para efectivo: acepta enteros o .00 (ej: 10, 12.00, 15.00), pero rechaza centavos (ej: 15.01)
  // - Para otros métodos: acepta cualquier monto decimal válido (ej: 15.01, 15.00, 15)
  const isAmountValid = isCashSelected ? (isValidNum && isIntegerOrZeroDecimal) : isValidNum;
  const hasDecimalError = isCashSelected && isValidNum && !isIntegerOrZeroDecimal;

  // Integración con el mensaje de validación nativo de HTML5 (Validation Bubble)
  useEffect(() => {
    if (!inputRef.current) return;

    if (hasDecimalError) {
      inputRef.current.setCustomValidity(
        'El pago en efectivo solo acepta números enteros o terminados en .00 (ej: 10 o 10.00). Montos con centavos como 15.01 no son permitidos.'
      );
    } else if (amountText && !isValidNum) {
      inputRef.current.setCustomValidity('Ingrese un monto válido mayor a 0.');
    } else {
      inputRef.current.setCustomValidity('');
    }
  }, [hasDecimalError, amountText, isValidNum]);

  const finalAmountBsS = isValidNum ? (isCashSelected ? Math.round(parsedAmount) : parsedAmount) : 0;
  const usdPreview = exchangeRate > 0 ? (finalAmountBsS / exchangeRate).toFixed(2) : '0.00';

  const handleSubmit = (e) => {
    e.preventDefault();

    // Disparar la burbuja de validación nativa de HTML5 si el campo es inválido
    if (inputRef.current && !inputRef.current.checkValidity()) {
      inputRef.current.reportValidity();
      return;
    }

    if (!selectedMethod || !isAmountValid || finalAmountBsS <= 0) return;

    if (selectedMethod.requiresReference && !reference.trim()) {
      alert(`El método de pago (${selectedMethod.name}) requiere un número de referencia.`);
      return;
    }

    onAddPayment({
      methodId: selectedMethod.id,
      methodName: selectedMethod.name,
      isCash: selectedMethod.isCash,
      amountBsS: finalAmountBsS,
      amountUsd: parseFloat(usdPreview),
      reference: reference.trim() || null,
    });

    setReference('');
  };

  return (
    <form className="payment-form" onSubmit={handleSubmit} noValidate={false}>
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
            ref={inputRef}
            type="text"
            inputMode="decimal"
            className={`form-input font-bold ${hasDecimalError ? 'is-invalid' : ''}`}
            placeholder={isCashSelected ? '10 o 10.00' : '0.00'}
            value={amountText}
            onChange={(e) => setAmountText(e.target.value)}
            onFocus={(e) => e.target.select()}
          />
          {isCashSelected && (
            <small
              className="form-text"
              style={{
                display: 'block',
                marginTop: '4px',
                fontSize: '0.75rem',
                color: hasDecimalError ? '#ef4444' : 'var(--text-muted)',
                fontWeight: hasDecimalError ? 600 : 400
              }}
            >
              {hasDecimalError
                ? 'El pago en efectivo solo acepta números enteros (ej: 10 o 10.00). Montos con centavos como 15.01 no son permitidos.'
                : 'El pago en efectivo acepta números enteros (ej: 10 o 10.00).'}
            </small>
          )}
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
        disabled={!isAmountValid}
      >
        <Plus size={16} /> Agregar Pago
      </button>
    </form>
  );
}
