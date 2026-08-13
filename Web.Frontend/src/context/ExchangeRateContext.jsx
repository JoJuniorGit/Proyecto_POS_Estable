import { createContext, useContext, useState, useEffect } from 'react';
import { api } from '../services/api';
import { connectRateHub, disconnectRateHub } from '../services/signalr';

const ExchangeRateContext = createContext();

export function ExchangeRateProvider({ children }) {
  const [exchangeRate, setExchangeRate] = useState(0);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let isMounted = true;

    async function initRate() {
      try {
        const data = await api.get('/api/exchange-rate/today');
        if (isMounted && data?.value) {
          setExchangeRate(data.value);
        }
      } catch (err) {
        console.warn('[ExchangeRate] Error al cargar la tasa inicial:', err.message);
      } finally {
        if (isMounted) setLoading(false);
      }
    }

    initRate();

    // Conectar SignalR
    connectRateHub((newRate) => {
      if (isMounted) {
        setExchangeRate(newRate);
      }
    });

    return () => {
      isMounted = false;
      disconnectRateHub();
    };
  }, []);

  return (
    <ExchangeRateContext.Provider value={{ exchangeRate, setExchangeRate, loading }}>
      {children}
    </ExchangeRateContext.Provider>
  );
}

export function useExchangeRate() {
  const context = useContext(ExchangeRateContext);
  if (!context) {
    throw new Error('useExchangeRate debe ser usado dentro de ExchangeRateProvider');
  }
  return context;
}
