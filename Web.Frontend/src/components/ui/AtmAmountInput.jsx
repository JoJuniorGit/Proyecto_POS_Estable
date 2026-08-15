import React, { useState, useEffect } from 'react';
import { formatNumberEs, parseFormattedNumber, formatAtmInput } from '../../utils/formatters';

export default function AtmAmountInput({
  value = '',
  onChange,
  placeholder = '0,00',
  className = 'input-field',
  style = {},
  disabled = false,
  autoFocus = false,
  prefix = '',
  allowDecimals = true,
  ...props
}) {
  const [displayValue, setDisplayValue] = useState('');
  const decimals = allowDecimals ? 2 : 0;

  useEffect(() => {
    if (value === '' || value === null || value === undefined) {
      setDisplayValue('');
    } else if (typeof value === 'number') {
      setDisplayValue(formatNumberEs(value, decimals));
    } else {
      const num = parseFormattedNumber(value);
      if (!isNaN(num) && num > 0) {
        setDisplayValue(formatNumberEs(num, decimals));
      } else {
        setDisplayValue(value);
      }
    }
  }, [value, decimals]);

  const handleChange = (e) => {
    const rawText = e.target.value;
    const formatted = formatAtmInput(rawText, decimals);
    const numeric = parseFormattedNumber(formatted);
    setDisplayValue(formatted);
    if (onChange) {
      onChange(numeric, formatted);
    }
  };

  const handleKeyDown = (e) => {
    if (e.key === 'Backspace' && displayValue) {
      const digits = displayValue.replace(/\D/g, '');
      const newDigits = digits.slice(0, -1);
      const formatted = formatAtmInput(newDigits, decimals);
      const numeric = parseFormattedNumber(formatted);
      setDisplayValue(formatted);
      if (onChange) {
        onChange(numeric, formatted);
      }
      e.preventDefault();
    }
  };

  // All amount input fields maintain the exact reference padding of the Divisas field (32px)
  const prefixPadding = prefix ? '32px' : (style.paddingLeft || '12px');

  return (
    <div style={{ position: 'relative', display: 'flex', alignItems: 'center', width: '100%' }}>
      {prefix && (
        <span 
          style={{ 
            position: 'absolute', 
            left: '8px', 
            fontSize: prefix === '$' ? '0.85rem' : '0.72rem', 
            fontWeight: 700,
            color: 'var(--text-muted)', 
            pointerEvents: 'none',
            userSelect: 'none',
            zIndex: 2,
            whiteSpace: 'nowrap'
          }}
        >
          {prefix}
        </span>
      )}
      <input
        type="text"
        inputMode={allowDecimals ? 'decimal' : 'numeric'}
        className={`atm-amount-input ${className}`}
        value={displayValue}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        placeholder={placeholder}
        disabled={disabled}
        autoFocus={autoFocus}
        style={{
          paddingLeft: prefixPadding,
          paddingRight: '14px',
          height: '42px',
          textAlign: style.textAlign || 'right',
          fontFamily: 'monospace',
          ...style
        }}
        {...props}
      />
    </div>
  );
}
