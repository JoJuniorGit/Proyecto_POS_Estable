import React, { useState, useEffect, useRef } from 'react';
import { useCurrencyFormat } from '../../context/CurrencyFormatContext';
import { amountToCents } from '../../utils/formatters';
import './AtmAmountInput.css';

export default function AtmAmountInput({
  value = '',
  onChange,
  placeholder,
  className = 'input-field',
  style = {},
  disabled = false,
  autoFocus = false,
  prefix = '',
  allowDecimals = true,
  onBlur: externalOnBlur,
  onFocus: externalOnFocus,
  ...props
}) {
  const { currencyFormat, formatAmount, parseAmount } = useCurrencyFormat();
  const [displayValue, setDisplayValue] = useState('');
  const isFocusedRef = useRef(false);
  const inputRef = useRef(null);
  const decimals = allowDecimals ? 2 : 0;

  // Placeholder por defecto según el formato oficial activo si no se pasa uno explícito
  const activePlaceholder = placeholder || (currencyFormat === 'Venezuelan' ? '0,00' : '0.00');

  // Sincronización con el valor externo cuando el campo NO está siendo editado activamente
  useEffect(() => {
    if (isFocusedRef.current) return;

    if (value === '' || value === null || value === undefined || value === 0 || value === '0') {
      setDisplayValue('');
    } else if (typeof value === 'number') {
      setDisplayValue(value > 0 ? formatAmount(value, decimals) : '');
    } else {
      const num = parseAmount(value);
      if (!isNaN(num) && num > 0) {
        setDisplayValue(formatAmount(num, decimals));
      } else {
        setDisplayValue('');
      }
    }
  }, [value, decimals, currencyFormat, formatAmount, parseAmount]);

  const handleChange = (e) => {
    const rawText = e.target.value;
    
    // Filtrar caracteres no numéricos excepto separadores permitidos (dígitos, punto y coma)
    // Permite que el usuario escriba libremente "172.786,94" o "172,786.94" o "150.5"
    const cleaned = rawText.replace(/[^\d.,]/g, '');
    setDisplayValue(cleaned);

    if (onChange) {
      // 8.9-M18: normalización canónica a centésimas (idéntica a PaymentForm).
      const numeric = amountToCents(cleaned) / 100;
      onChange(numeric, cleaned);
    }
  };

  const handleFocus = (e) => {
    isFocusedRef.current = true;
    // Seleccionar todo el contenido al entrar para facilitar sobreescritura natural
    e.target.select();
    if (externalOnFocus) {
      externalOnFocus(e);
    }
  };

  const handleBlur = (e) => {
    isFocusedRef.current = false;
    const rawText = displayValue.trim();

    if (!rawText) {
      setDisplayValue('');
      if (onChange) onChange(0, '');
    } else {
      // 8.9-M18: misma normalización en centésimas que en handleChange.
      const num = amountToCents(rawText) / 100;
      if (!isNaN(num) && num > 0) {
        const formatted = formatAmount(num, decimals);
        setDisplayValue(formatted);
        if (onChange) onChange(num, formatted);
      } else {
        setDisplayValue('');
        if (onChange) onChange(0, '');
      }
    }

    if (externalOnBlur) {
      externalOnBlur(e);
    }
  };

  // Espaciado para el prefijo de moneda ($ o Bs.S)
  const prefixPadding = prefix ? '32px' : (style.paddingLeft || '12px');

  return (
    <div className="atm-input-wrapper">
      {prefix && (
        <span 
          className={`atm-prefix ${prefix === '$' ? 'atm-prefix-sign' : 'atm-prefix-text'}`}
        >
          {prefix}
        </span>
      )}
      <input
        ref={inputRef}
        type="text"
        inputMode={allowDecimals ? 'decimal' : 'numeric'}
        className={`atm-amount-input ${className} atm-field`}
        value={displayValue}
        onChange={handleChange}
        onFocus={handleFocus}
        onBlur={handleBlur}
        placeholder={activePlaceholder}
        disabled={disabled}
        autoFocus={autoFocus}
        aria-label={props['aria-label'] || `Monto en ${prefix || 'dinero'}`}
        style={{
          paddingLeft: prefixPadding,
          textAlign: style.textAlign || 'right',
          ...style
        }}
        {...props}
      />
    </div>
  );
}
