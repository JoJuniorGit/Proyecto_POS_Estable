import { useState, useEffect, useCallback } from 'react';
import { api } from '../services/api';
import { ClipboardCheck, Loader2, RefreshCw } from 'lucide-react';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { formatBsS, formatUSD } from '../utils/formatters';

export default function ClosingPage() {
  const [expectedTotals, setExpectedTotals] = useState([]);
  const [loading, setLoading] = useState(false);
  const { exchangeRate } = useExchangeRate();

  const loadExpected = useCallback(async () => {
    setLoading(true);
    try {
      const todayUtc = new Date().toISOString();
      const data = await api.get(`/api/dailyclosure/expected-totals?dateUtc=${encodeURIComponent(todayUtc)}`);
      setExpectedTotals(data || []);
    } catch (err) {
      console.error('[ClosingPage] Error cargando totales:', err);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadExpected();
  }, [loadExpected]);

  return (
    <div className="closing-page" style={{ maxWidth: '800px', margin: '0 auto' }}>
      <div className="page-header flex-between mb-4">
        <h2 className="page-title flex-align-center gap-2">
          <ClipboardCheck size={24} /> Arqueo y Cierre Diario de Caja
        </h2>
        <button
          type="button"
          className="btn btn-outline btn-sm flex-align-center gap-2"
          onClick={loadExpected}
          disabled={loading}
        >
          <RefreshCw size={16} className={loading ? 'animate-spin' : ''} /> Actualizar
        </button>
      </div>

      <div className="card mb-4">
        <h3 className="card-title mb-3">Totales Esperados por Método de Pago</h3>

        {loading ? (
          <div className="p-4 text-center text-muted">
            <Loader2 className="animate-spin" size={24} /> Calculando totales esperados...
          </div>
        ) : expectedTotals.length === 0 ? (
          <div className="p-4 text-center text-muted">No hay movimientos registrados hoy.</div>
        ) : (
          <table className="cart-table">
            <thead>
              <tr>
                <th>Método de Pago</th>
                <th className="text-right">Esperado Bs.S</th>
                <th className="text-right">Esperado USD</th>
              </tr>
            </thead>
            <tbody>
              {expectedTotals.map((item, idx) => {
                const amtBsS = item.expectedAmountBsS ?? item.expectedBsS ?? item.amountLocal ?? 0;
                const amtUsd = amtBsS / (exchangeRate || 1);
                return (
                  <tr key={idx}>
                    <td className="font-medium">{item.paymentMethodName || item.methodName || 'Método'}</td>
                    <td className="text-right font-bold color-primary">
                      {formatBsS(amtBsS)}
                    </td>
                    <td className="text-right">
                      {formatUSD(amtUsd)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
