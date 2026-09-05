import { createContext, useContext, useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { 
  getCurrencyFormat, 
  setCachedCurrencyFormat, 
  formatAmount as formatAmountUtil, 
  formatBsS as formatBsSUtil, 
  formatUSD as formatUSDUtil, 
  parseAmount as parseAmountUtil 
} from '../utils/formatters';

const CurrencyFormatContext = createContext();

export function CurrencyFormatProvider({ children }) {
  const [currencyFormat, setFormatState] = useState(() => getCurrencyFormat());
  const [isLoaded, setIsLoaded] = useState(false);

  // Carga inicial desde la API del backend
  const fetchFormat = useCallback(async () => {
    try {
      const res = await api.get('/api/settings/currency-format');
      if (res?.format) {
        const val = (res.format === 'International') ? 'International' : 'Venezuelan';
        setFormatState(val);
        setCachedCurrencyFormat(val);
      }
    } catch (err) {
      console.warn('[CurrencyFormatContext] Error al cargar preferencia de moneda del servidor:', err?.message || err);
    } finally {
      setIsLoaded(true);
    }
  }, []);

  useEffect(() => {
    fetchFormat();

    // Escuchar actualizaciones en tiempo real (SignalR o eventos locales)
    const handleUpdate = (event) => {
      const newFormat = event.detail;
      if (newFormat === 'Venezuelan' || newFormat === 'International') {
        setFormatState(newFormat);
        setCachedCurrencyFormat(newFormat);
      }
    };

    window.addEventListener('onCurrencyFormatUpdated', handleUpdate);
    return () => {
      window.removeEventListener('onCurrencyFormatUpdated', handleUpdate);
    };
  }, [fetchFormat]);

  // Función para actualizar y persistir la preferencia en base de datos
  const updateCurrencyFormat = useCallback(async (newFormat) => {
    const val = (newFormat === 'International') ? 'International' : 'Venezuelan';
    // Actualización optimista local
    setFormatState(val);
    setCachedCurrencyFormat(val);

    try {
      await api.put('/api/settings/currency-format', { format: val });
    } catch (err) {
      console.error('[CurrencyFormatContext] Error al persistir formato en servidor:', err);
      // Revertir en caso de error severo
      fetchFormat();
      throw err;
    }
  }, [fetchFormat]);

  const formatAmount = useCallback((val, decimals = 2) => {
    return formatAmountUtil(val, currencyFormat, decimals);
  }, [currencyFormat]);

  const formatBsS = useCallback((val, decimals = 2) => {
    return formatBsSUtil(val, decimals, currencyFormat);
  }, [currencyFormat]);

  const formatUSD = useCallback((val, decimals = 2) => {
    return formatUSDUtil(val, decimals, currencyFormat);
  }, [currencyFormat]);

  const parseAmount = useCallback((val) => {
    return parseAmountUtil(val);
  }, []);

  const value = {
    currencyFormat,
    setCurrencyFormat: updateCurrencyFormat,
    formatAmount,
    formatBsS,
    formatUSD,
    parseAmount,
    isLoaded
  };

  return (
    <CurrencyFormatContext.Provider value={value}>
      {children}
    </CurrencyFormatContext.Provider>
  );
}

export function useCurrencyFormat() {
  const context = useContext(CurrencyFormatContext);
  if (!context) {
    // Si se usa fuera del Provider, fallback seguro a utilidades
    const current = getCurrencyFormat();
    return {
      currencyFormat: current,
      setCurrencyFormat: (fmt) => setCachedCurrencyFormat(fmt),
      formatAmount: (val, dec = 2) => formatAmountUtil(val, current, dec),
      formatBsS: (val, dec = 2) => formatBsSUtil(val, dec, current),
      formatUSD: (val, dec = 2) => formatUSDUtil(val, dec, current),
      parseAmount: parseAmountUtil,
      isLoaded: true
    };
  }
  return context;
}
