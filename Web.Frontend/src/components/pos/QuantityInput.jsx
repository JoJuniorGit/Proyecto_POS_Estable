import { useState, useEffect } from 'react';

export default function QuantityInput({ item, value, isFractional: isFractionalProp, onUpdateQty, onChange, style }) {
  const isFractional = Boolean(
    isFractionalProp ?? (
      item?.isFractional ||
      item?.isFractionable ||
      item?.IsFractional ||
      item?.IsFractionable ||
      (item?.unitOfMeasure && item.unitOfMeasure !== 'Und' && item.unitOfMeasure !== 0)
    )
  );
  const currentQty = item?.quantity !== undefined && item?.quantity !== null ? item.quantity : (value !== undefined && value !== null ? value : '');
  const itemId = item?.id ?? item?.productId ?? 0;
  const [localVal, setLocalVal] = useState(String(currentQty ?? ''));

  useEffect(() => {
    // Synchronize local input state when quantity changes externally
    if (currentQty !== undefined && currentQty !== null && currentQty !== '') {
      setLocalVal(String(currentQty));
    }
  }, [currentQty]);

  const fireQtyChange = (newQty) => {
    if (onUpdateQty) onUpdateQty(itemId, newQty);
    if (onChange) onChange(newQty);
  };

  const handleChange = (e) => {
    const inputStr = e.target.value;

    // 1. Allow blank string while user erases to type a new value
    if (inputStr === '') {
      setLocalVal('');
      if (onChange) onChange('');
      return;
    }

    if (!isFractional) {
      // Para productos NO fraccionables: SOLO dígitos enteros, sin puntos ni comas
      if (!/^\d+$/.test(inputStr)) return;

      setLocalVal(inputStr);
      const parsedInt = parseInt(inputStr, 10);
      if (!isNaN(parsedInt) && parsedInt > 0) {
        fireQtyChange(parsedInt);
      }
      return;
    }

    // Para productos fraccionables: permitir dígitos y hasta 3 decimales (. o ,)
    if (!/^\d*[,.]?\d{0,3}$/.test(inputStr)) return;
    setLocalVal(inputStr);

    // Partial inputs like "." or "," or "1." should stay in local state without firing backend call yet
    if (inputStr === '.' || inputStr === ',' || inputStr.endsWith('.') || inputStr.endsWith(',')) {
      return;
    }

    const normalized = inputStr.replace(',', '.');
    const parsedFloat = parseFloat(normalized);
    if (!isNaN(parsedFloat) && parsedFloat > 0) {
      const rounded = Math.round(parsedFloat * 1000) / 1000;
      fireQtyChange(rounded);
    }
  };

  const handleBlur = () => {
    const defaultQty = 1;

    if (!localVal || localVal === '.' || localVal === ',') {
      const fallbackVal = (typeof currentQty === 'number' && currentQty > 0)
        ? (!isFractional ? Math.max(1, Math.trunc(currentQty)) : currentQty)
        : defaultQty;
      setLocalVal(String(fallbackVal));
      if (currentQty !== fallbackVal) {
        fireQtyChange(fallbackVal);
      }
      return;
    }

    const normalized = localVal.replace(',', '.');
    const parsed = parseFloat(normalized);

    if (isNaN(parsed) || parsed <= 0) {
      setLocalVal(String(defaultQty));
      fireQtyChange(defaultQty);
    } else if (!isFractional) {
      const intVal = Math.max(1, Math.trunc(parsed));
      setLocalVal(String(intVal));
      fireQtyChange(intVal);
    } else {
      const rounded = Math.round(parsed * 1000) / 1000;
      setLocalVal(String(rounded));
      fireQtyChange(rounded);
    }
  };

  const handleKeyDown = (e) => {
    // Si el producto no es fraccionable, bloquear activamente las teclas de punto y coma
    if (!isFractional && (e.key === '.' || e.key === ',' || e.key === 'Decimal')) {
      e.preventDefault();
      return;
    }
    if (e.key === 'Enter') {
      e.target.blur();
    }
  };

  return (
    <input
      type="text"
      inputMode={isFractional ? "decimal" : "numeric"}
      className="qty-val-input"
      value={localVal}
      onChange={handleChange}
      onBlur={handleBlur}
      onKeyDown={handleKeyDown}
      onClick={(e) => {
        e.stopPropagation();
        e.target.select();
      }}
      style={style || {
        width: '56px',
        textAlign: 'center',
        border: '1px solid var(--border)',
        borderRadius: '4px',
        padding: '2px 4px',
        fontSize: '0.875rem',
        fontWeight: 'bold',
        backgroundColor: 'var(--bg-input, var(--bg-card))',
        color: 'var(--text-primary)'
      }}
    />
  );
}
