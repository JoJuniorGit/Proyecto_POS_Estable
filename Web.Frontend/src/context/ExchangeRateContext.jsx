import { createContext, useContext, useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { connectRateHub, disconnectRateHub } from '../services/signalr';

const ExchangeRateContext = createContext();

export function ExchangeRateProvider({ children }) {
  const [exchangeRate, setExchangeRate] = useState(0);
  const [lastUpdated, setLastUpdated] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let isMounted = true;

    async function initRate() {
      try {
        const data = await api.get('/api/exchange-rate/today');
        if (isMounted && data?.value) {
          setExchangeRate(data.value);
          if (data?.updatedAt || data?.UpdatedAt) {
            setLastUpdated(data.updatedAt || data.UpdatedAt);
          }
        }
      } catch (err) {
        console.warn('[ExchangeRate] Error al cargar la tasa inicial:', err.message);
      } finally {
        if (isMounted) setLoading(false);
      }
    }

    initRate();

    // Conectar SignalR
    connectRateHub(
      (newRate) => {
        if (isMounted) {
          setExchangeRate(newRate);
          setLastUpdated(new Date().toISOString());
        }
      },
      () => {
        if (isMounted) {
          window.dispatchEvent(new CustomEvent('onHoldSalesUpdated'));
        }
      },
      () => {
        if (isMounted) {
          window.dispatchEvent(new CustomEvent('onPaymentMethodsUpdated'));
        }
      }
    );

    return () => {
      isMounted = false;
      disconnectRateHub();
    };
  }, []);

  const isRateOutdated = !lastUpdated || (Date.now() - new Date(lastUpdated).getTime() > 24 * 60 * 60 * 1000);

  // 8.6-M1: F5 ahora expone syncBcvRate real. Llama al endpoint que raspa el portal BCV y
  // re-difunde la tasa vía SignalR a todos los clientes.
  const syncBcvRate = useCallback(async () => {
    const data = await api.post('/api/exchange-rate/sync-bcv');
    if (data?.Value || data?.value) {
      const newRate = data.Value || data.value;
      setExchangeRate(newRate);
      setLastUpdated(data.UpdatedAt || data.updatedAt || new Date().toISOString());
    }
    return data?.Value || data?.value || 0;
  }, []);

  return (
    <ExchangeRateContext.Provider value={{ exchangeRate, setExchangeRate, lastUpdated, setLastUpdated, isRateOutdated, loading, syncBcvRate }}>
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
