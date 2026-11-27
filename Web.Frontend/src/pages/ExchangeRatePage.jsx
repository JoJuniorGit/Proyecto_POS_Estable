import { useState, useEffect } from 'react';
import { api } from '../services/api';
import { useAuth } from '../context/AuthContext';
import { useExchangeRate } from '../context/ExchangeRateContext';
import { useMonetaryProfile } from '../context/MonetaryProfileContext';
import { normalizeRole } from '../navigation/roleViews';
import { displayCurrencyCode } from '../utils/monetaryProfile';
import { formatNumberEs } from '../utils/formatters';
import { resolveLocalCurrencyProfile } from '../components/register/registerFormLogic';
import { RefreshCw, Save, Loader2, History } from 'lucide-react';
import RoleGuard from '../navigation/RoleGuard';
import './ExchangeRatePage.css';

export const HISTORY_LIMIT = 90;
export const EXCHANGE_RATE_HISTORY_ENDPOINT = `/api/exchange-rate/history?limit=${HISTORY_LIMIT}`;

const OUTDATED_NOTICE = 'Tasa desactualizada: no se ha registrado una actualización en más de 24 horas. Sincronice con el BCV o ingrese la tasa oficial del día.';
const READ_ONLY_NOTICE = 'Modo Solo Lectura: Su cuenta tiene permisos de consulta. Los cambios e importaciones están bloqueados.';
const SYNC_ERROR_FALLBACK = 'No se pudo sincronizar la tasa con el BCV. Verifique la conexión o ingrese la tasa manualmente.';
const SAVE_SUCCESS_MESSAGE = 'Tasa de cambio actualizada correctamente.';
const VENEZUELA_TIME_ZONE = 'America/Caracas';
const EMPTY_VALUE = '—';

/** El espejo Web del RBAC: solo Admin escribe la tasa (Manager queda en solo lectura). */
export function canMutateExchangeRate(role) {
  return normalizeRole(role) === 'Admin';
}

function formatPart(parts, type) {
  return parts.find((part) => part.type === type)?.value || '';
}

/**
 * Formatea un instante UTC en la zona de Venezuela como `dd/MM/yyyy · hh:mm a. m.`.
 * @param {string|Date|null|undefined} value
 * @returns {string|null}
 */
export function formatCaracasUpdatedAt(value) {
  if (!value) return null;
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return null;

  try {
    const parts = new Intl.DateTimeFormat('es-VE', {
      timeZone: VENEZUELA_TIME_ZONE,
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      hour12: true,
    }).formatToParts(date);

    const day = formatPart(parts, 'day');
    const month = formatPart(parts, 'month');
    const year = formatPart(parts, 'year');
    const hour = formatPart(parts, 'hour');
    const minute = formatPart(parts, 'minute');
    const period = formatPart(parts, 'dayPeriod').replace(/\u00a0/g, ' ');

    if (!day || !month || !year || !hour || !minute) return null;
    return `${day}/${month}/${year} · ${hour}:${minute}${period ? ` ${period}` : ''}`;
  } catch {
    return null;
  }
}

function formatHistoryDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(value ?? ''));
  return match ? `${match[3]}/${match[2]}/${match[1]}` : EMPTY_VALUE;
}

function formatHistoryUpdatedAt(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})/.exec(String(value ?? ''));
  return match ? `${match[3]}/${match[2]}/${match[1]} ${match[4]}:${match[5]}` : EMPTY_VALUE;
}

function toFiniteNumber(value) {
  if (value === null || value === undefined || value === '') return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

/**
 * Mapea el contrato `date`/`rate`/`updatedAtLocal` a filas presentacionales del historial.
 * Ordena descendente por fecha (defensivo), limita a 90 filas y calcula la variación contra la
 * fila inmediatamente anterior (más antigua). La primera fila es la tasa `Vigente`.
 * @param {Array<object>} history
 * @returns {Array<object>}
 */
export function buildHistoryRows(history) {
  const normalized = (Array.isArray(history) ? history : [])
    .map((item) => ({
      date: item?.date ?? item?.Date ?? null,
      rate: toFiniteNumber(item?.rate ?? item?.Rate),
      updatedAtLocal: item?.updatedAtLocal ?? item?.UpdatedAtLocal ?? null,
    }))
    .filter((item) => item.date !== null && item.date !== undefined && item.rate !== null);

  normalized.sort((left, right) => String(right.date).localeCompare(String(left.date)));
  const visible = normalized.slice(0, HISTORY_LIMIT);

  return visible.map((item, index) => {
    const previous = index + 1 < visible.length ? visible[index + 1] : null;
    const variation = previous ? item.rate - previous.rate : null;
    const isCurrent = index === 0;

    return {
      date: item.date,
      rate: item.rate,
      updatedAtLocal: item.updatedAtLocal,
      dateDisplay: formatHistoryDate(item.date),
      rateDisplay: formatNumberEs(item.rate, 4),
      variationDisplay: variation === null
        ? EMPTY_VALUE
        : variation > 0
          ? `+${formatNumberEs(variation, 4)}`
          : formatNumberEs(variation, 4),
      variationTone: variation === null
        ? 'muted'
        : variation > 0 ? 'positive' : variation < 0 ? 'negative' : 'neutral',
      updatedAtDisplay: formatHistoryUpdatedAt(item.updatedAtLocal),
      isCurrent,
      statusLabel: isCurrent ? 'Vigente' : 'Histórica',
    };
  });
}

/**
 * Tabla presentacional del historial (sin efectos) para poder montarla en tests SSR.
 */
export function ExchangeRateHistory({ rows = [], loading = false }) {
  return (
    <section className="card xr-history-card">
      <div className="xr-history-header">
        <span className="xr-history-title">
          <History size={18} aria-hidden="true" />
          Historial de Tasas
        </span>
        <span className="text-muted xr-history-count">{rows.length} registros</span>
      </div>

      {loading ? (
        <div className="p-4 text-center text-muted">Cargando historial...</div>
      ) : rows.length === 0 ? (
        <div className="p-4 text-center text-muted">No hay registros de cambios de tasa.</div>
      ) : (
        <div className="xr-table-scroll">
          <table className="cart-table xr-history-table">
            <thead>
              <tr>
                <th>FECHA</th>
                <th className="text-right">TASA</th>
                <th className="text-right">VARIACIÓN</th>
                <th>ACTUALIZACIÓN</th>
                <th>ESTADO</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => (
                <tr
                  key={`${row.date}-${index}`}
                  className={row.isCurrent ? 'xr-history-current' : undefined}
                >
                  <td>{row.dateDisplay}</td>
                  <td className="text-right xr-rate-cell">{row.rateDisplay}</td>
                  <td className={`text-right xr-variation xr-variation-${row.variationTone}`}>
                    {row.variationDisplay}
                  </td>
                  <td>{row.updatedAtDisplay}</td>
                  <td>
                    <span className={`xr-status-pill ${row.isCurrent ? 'xr-status-vigente' : 'xr-status-historica'}`}>
                      {row.statusLabel}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export default function ExchangeRatePage() {
  return (
    <RoleGuard view="exchange" message="No tienes los permisos necesarios para modificar la Tasa de Cambio.">
      <ExchangeRatePageContent />
    </RoleGuard>
  );
}

function ExchangeRatePageContent() {
  const { user } = useAuth();
  const { exchangeRate, setExchangeRate, lastUpdated, setLastUpdated, isRateOutdated } = useExchangeRate();
  const { profile: monetaryProfile, formatSecondary } = useMonetaryProfile();
  const local = resolveLocalCurrencyProfile(monetaryProfile);
  const isDual = local.isDual;
  const primaryLabel = displayCurrencyCode(monetaryProfile.primaryCurrencyCode) || 'USD';
  const localLabel = local.label || 'Bs.S';
  const canMutate = canMutateExchangeRate(user?.role);
  const lastUpdatedDisplay = formatCaracasUpdatedAt(lastUpdated);

  const [newRateText, setNewRateText] = useState('');
  const [historyRows, setHistoryRows] = useState([]);
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
      const data = await api.get(EXCHANGE_RATE_HISTORY_ENDPOINT);
      setHistoryRows(buildHistoryRows(data));
    } catch (err) {
      console.error('[ExchangeRatePage] Error al cargar historial:', err);
    } finally {
      setLoadingHistory(false);
    }
  };

  useEffect(() => {
    if (isDual) loadHistory();
  }, [isDual]);

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
        setLastUpdated(res.updatedAt || res.UpdatedAt || new Date().toISOString());
        setMessage({ type: 'success', text: SAVE_SUCCESS_MESSAGE });
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
        setLastUpdated(res.updatedAt || res.UpdatedAt || new Date().toISOString());
        setMessage({
          type: 'success',
          text: `Tasa sincronizada con BCV: ${formatSecondary(res.value, 4)}`,
        });
        loadHistory();
      }
    } catch (err) {
      console.error('[ExchangeRatePage] Error sincronizando BCV:', err);
      setMessage({
        type: 'danger',
        text: err.message || SYNC_ERROR_FALLBACK,
        canRetry: true,
      });
    } finally {
      setIsSyncing(false);
    }
  };

  // 8.211-T4b2b2b (S9): `Single` no opera tasa de cambio; se muestra un estado explícito y se
  // ocultan los controles de tasa actual/manual/sincronización/historial sin tocar el backend.
  if (!isDual) {
    return (
      <div className="exchange-page xr-container">
        <h2 className="page-title mb-4">Tasa de Cambio</h2>
        <div className="alert alert-info mb-3 text-sm" role="status">
          {`El régimen monetario Single no utiliza tasa de cambio: los importes se expresan únicamente en ${localLabel}. No hay sincronización BCV ni carga manual disponibles.`}
        </div>
      </div>
    );
  }

  return (
    <div className="exchange-page xr-container">
      <header className="xr-header mb-4">
        <h2 className="page-title">Tasa de Cambio</h2>
        <div className="xr-subtitle-row">
          <p className="xr-subtitle">
            Sincronización automática cada 2 horas. También puede sincronizar a demanda o ingresar la tasa del día manualmente.
          </p>
          <span className="xr-badge">Modo Híbrido</span>
        </div>
      </header>

      {isRateOutdated && (
        <div className="alert alert-warning mb-3 xr-warning-bordered" role="alert">
          {OUTDATED_NOTICE}
        </div>
      )}

      {!canMutate && (
        <div className="alert alert-warning mb-3 xr-read-only-banner" role="status">
          {READ_ONLY_NOTICE}
        </div>
      )}

      {message && (
        <div className={`alert alert-${message.type} mb-4`} role="status">
          <div>{message.text}</div>
          {canMutate && message.canRetry && (
            <div className="mt-2">
              <button
                type="button"
                className="btn btn-outline btn-sm"
                onClick={handleSyncBcv}
                disabled={isSyncing}
              >
                {isSyncing ? <Loader2 className="animate-spin" size={14} /> : <RefreshCw size={14} />}
                Reintentar
              </button>
            </div>
          )}
        </div>
      )}

      <div className="card mb-4 xr-card">
        <div className="xr-current-rate">
          <span className="xr-section-label">TASA VIGENTE</span>
          <div className="xr-rate-equation">
            {`1 ${primaryLabel} = ${formatNumberEs(exchangeRate || 0, 4)} ${localLabel}`}
          </div>
          <div className="xr-updated-at">
            <span
              className={`xr-status-dot ${isRateOutdated ? 'xr-status-dot-warning' : 'xr-status-dot-ok'}`}
              aria-hidden="true"
            />
            <span className="text-muted">
              {lastUpdatedDisplay ? `Actualizada: ${lastUpdatedDisplay}` : 'Sin actualización registrada'}
            </span>
          </div>
        </div>

        {canMutate && (
          <form onSubmit={handleSaveManual} className="xr-manual-form">
            <label className="form-label" htmlFor="xr-new-rate">Nueva tasa del día</label>
            <div className="xr-manual-controls">
              <span className="xr-manual-prefix">1 {primaryLabel} =</span>
              <input
                id="xr-new-rate"
                type="number"
                step="0.01"
                min="0.01"
                className="form-input xr-rate-input"
                value={newRateText}
                onChange={(e) => setNewRateText(e.target.value)}
                required
              />
              <span className="xr-manual-suffix">{localLabel}</span>
              <button type="submit" className="btn btn-primary" disabled={isSaving}>
                {isSaving ? <Loader2 className="animate-spin" size={16} /> : <Save size={16} />}
                Guardar
              </button>
              <button type="button" className="btn btn-outline" onClick={handleSyncBcv} disabled={isSyncing}>
                {isSyncing ? <Loader2 className="animate-spin" size={16} /> : <RefreshCw size={16} />}
                Sincronizar BCV
              </button>
            </div>
          </form>
        )}
      </div>

      <ExchangeRateHistory rows={historyRows} loading={loadingHistory} />
    </div>
  );
}
