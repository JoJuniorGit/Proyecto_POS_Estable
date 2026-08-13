import { useState, useEffect } from 'react';

export default function QuantityInput({ item, onUpdateQty, style }) {
  const [localVal, setLocalVal] = useState(String(item?.quantity ?? ''));

  useEffect(() => {
    // Synchronize local input state when item quantity changes externally (e.g. + / - buttons or server response)
    if (item?.quantity !== undefined && item?.quantity !== null && item?.quantity !== '') {
      setLocalVal(String(item.quantity));
    }
  }, [item?.quantity]);

  const handleChange = (e) => {
    const inputStr = e.target.value;

    // 1. Allow blank string while user erases to type a new value
    if (inputStr === '') {
      setLocalVal('');
      return;
    }

    const isFrac = item?.isFractional;

    if (!isFrac) {
      // Non-fractional: strictly digits '0'-'9'
      if (!/^\d*$/.test(inputStr)) return;
      setLocalVal(inputStr);

      const parsedInt = parseInt(inputStr, 10);
      if (!isNaN(parsedInt) && parsedInt > 0) {
        onUpdateQty(item.id, parsedInt);
      }
    } else {
      // Fractional: digits, max 1 decimal separator (. or ,), max 3 decimals
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
        onUpdateQty(item.id, rounded);
      }
    }
  };

  const handleBlur = () => {
    const isFrac = item?.isFractional;
    const defaultQty = isFrac ? 0.001 : 1;

    if (!localVal || localVal === '.' || localVal === ',') {
      const fallbackVal = item?.quantity > 0 ? item.quantity : defaultQty;
      setLocalVal(String(fallbackVal));
      if (item?.quantity !== fallbackVal) {
        onUpdateQty(item.id, fallbackVal);
      }
      return;
    }

    const normalized = localVal.replace(',', '.');
    const parsed = isFrac ? parseFloat(normalized) : parseInt(normalized, 10);

    if (isNaN(parsed) || parsed <= 0) {
      setLocalVal(String(defaultQty));
      onUpdateQty(item.id, defaultQty);
    } else {
      const rounded = isFrac ? Math.round(parsed * 1000) / 1000 : parsed;
      setLocalVal(String(rounded));
      onUpdateQty(item.id, rounded);
    }
  };

  return (
    <input
      type="text"
      inputMode="decimal"
      className="qty-val-input"
      value={localVal}
      onChange={handleChange}
      onBlur={handleBlur}
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
