import { useState, useEffect } from 'react';
import { api } from '../services/api';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { DollarSign, RefreshCw, Save, Loader2, History } from 'lucide-react';
import { formatBsS, formatNumberEs } from '../utils/formatters';

export default function ExchangeRatePage() {
  const { exchangeRate, setExchangeRate, lastUpdated, isRateOutdated } = useExchangeRate();
  const [newRateText, setNewRateText] = useState('');
  const [history, setHistory] = useState([]);
  const [loadingHistory, setLoadingHistory] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [isSyncing, setIsSyncing] = useState(false);
  const [message, setMessage] = useState(null);

  useEffect(() => {
    if (exchangeRate > 0) {
      setNewRateText(exchangeRate.toFixed(2));
    }
  }, [exchangeRate]);

  const loadHistory = async () => {
    setLoadingHistory(true);
    try {
      const data = await api.get('/api/exchange-rate/history');
      setHistory(data || []);
    } catch (err) {
      console.error('[ExchangeRatePage] Error al cargar historial:', err);
    } finally {
      setLoadingHistory(false);
    }
  };

  useEffect(() => {
    loadHistory();
  }, []);

  const handleSaveManual = async (e) => {
    e.preventDefault();
    const val = parseFloat(newRateText);
    if (!val || val <= 0) return;

    setIsSaving(true);
    setMessage(null);
    try {
      const today = new Date().toISOString().split('T')[0];
      const res = await api.post('/api/exchange-rate', { value: val, date: today });
      if (res?.value) {
        setExchangeRate(res.value);
        setMessage({ type: 'success', text: 'Tasa de cambio actualizada correctamente.' });
        loadHistory();
      }
    } catch (err) {
      console.error('[ExchangeRatePage] Error guardando tasa:', err);
      setMessage({ type: 'danger', text: err.message || 'No se pudo guardar la tasa.' });
    } finally {
      setIsSaving(false);
    }
  };

  const handleSyncBcv = async () => {
    setIsSyncing(true);
    setMessage(null);
    try {
      const res = await api.post('/api/exchange-rate/sync-bcv');
      if (res?.value) {
        setExchangeRate(res.value);
        setNewRateText(res.value.toFixed(2));
        setMessage({ type: 'success', text: `Tasa sincronizada con BCV: ${formatBsS(res.value)}` });
        loadHistory();
      }
    } catch (err) {
      console.error('[ExchangeRatePage] Error sincronizando BCV:', err);
      setMessage({
        type: 'danger',
        text: err.message || 'No se pudo sincronizar la tasa con el BCV. Verifique la conexión o ingrese la tasa manualmente.',
        canRetry: true,
      });
    } finally {
      setIsSyncing(false);
    }
  };

  return (
    <div className="exchange-page" style={{ maxWidth: '800px', margin: '0 auto' }}>
      <h2 className="page-title mb-4">Gestión de Tasa de Cambio</h2>

      {/* Modo Manual Informativo */}
      <div className="alert alert-info mb-3" style={{ fontSize: '0.875rem' }}>
        ℹ️ La tasa de cambio opera en <strong>modo manual</strong>. Puede sincronizar con el BCV a demanda o ingresar la tasa oficial de la jornada directamente.
      </div>

      {/* Alerta de Desactualización (> 24h) */}
      {isRateOutdated && (
        <div className="alert alert-warning mb-3" style={{ fontSize: '0.875rem', borderColor: '#f59e0b' }}>
          ⚠️ <strong>Tasa desactualizada:</strong> No se ha registrado actualización en más de 24 horas. Por favor sincronice con el BCV o establezca la tasa del día.
        </div>
      )}

      {message && (
        <div className={`alert alert-${message.type} mb-4`}>
          <div>{message.text}</div>
          {message.canRetry && (
            <div className="mt-2 flex-align-center gap-2">
              <button
                type="button"
                className="btn btn-outline btn-sm"
                onClick={handleSyncBcv}
                disabled={isSyncing}
              >
                {isSyncing ? <Loader2 className="animate-spin" size={14} /> : <RefreshCw size={14} />}
                Reintentar Sincronización
              </button>
              <span className="text-muted" style={{ fontSize: '0.8rem' }}>
                o puede ingresar la tasa manualmente abajo
              </span>
            </div>
          )}
        </div>
      )}

      {/* Tasa Actual & Acciones */}
      <div className="card mb-4">
        <div className="flex-between align-center mb-3">
          <span className="font-medium text-muted">Tasa Actual del Sistema</span>
          <button
            type="button"
            className="btn btn-outline btn-sm"
            onClick={handleSyncBcv}
            disabled={isSyncing}
          >
            {isSyncing ? (
              <>
                <Loader2 className="animate-spin" size={14} /> Sincronizando...
              </>
            ) : (
              <>
                <RefreshCw size={14} /> Sincronizar BCV (Oficial)
              </>
            )}
          </button>
        </div>

        <div className="current-rate-display mb-4">
          <DollarSign size={32} className="color-primary" />
          <span className="current-rate-value">
            Bs.S {exchangeRate > 0 ? formatNumberEs(exchangeRate) : '---'}
          </span>
          <span className="text-muted" style={{ fontSize: '0.9rem' }}>/ 1.00 USD</span>
        </div>

        <form onSubmit={handleSaveManual} className="form-row align-end">
          <div className="form-group flex-1">
            <label className="form-label">Establecer Tasa Manual (Bs.S por USD)</label>
            <input
              type="number"
              step="0.01"
              min="0.01"
              className="form-input font-bold"
              value={newRateText}
              onChange={(e) => setNewRateText(e.target.value)}
              required
            />
          </div>
          <div className="form-group">
            <button type="submit" className="btn btn-primary" disabled={isSaving}>
              {isSaving ? <Loader2 className="animate-spin" size={16} /> : <Save size={16} />}
              Guardar Tasa
            </button>
          </div>
        </form>
      </div>

      {/* Historial de Tasas */}
      <div className="card padding-none overflow-hidden">
        <div className="p-3 border-bottom font-bold flex-align-center gap-2">
          <History size={18} /> Historial Reciente de Tasas
        </div>

        {loadingHistory ? (
          <div className="p-4 text-center text-muted">Cargando historial...</div>
        ) : history.length === 0 ? (
          <div className="p-4 text-center text-muted">No hay registros de cambios de tasa.</div>
        ) : (
          <table className="cart-table">
            <thead>
              <tr>
                <th>Fecha</th>
                <th className="text-right">Tasa (Bs.S / USD)</th>
              </tr>
            </thead>
            <tbody>
              {history.slice(0, 10).map((h, i) => (
                <tr key={i}>
                  <td>{h.date || h.Date || '-'}</td>
                  <td className="text-right font-bold color-primary">
                    {formatBsS(h.value || h.Value || h.rate || h.Rate || 0)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
