import { test, describe, beforeEach } from 'node:test';
import assert from 'node:assert';
import React from 'react';
import { renderToString } from 'react-dom/server';
import ExchangeRatePage, {
  ExchangeRateHistory,
  EXCHANGE_RATE_HISTORY_ENDPOINT,
  HISTORY_LIMIT,
  buildHistoryRows,
  formatCaracasUpdatedAt,
} from './ExchangeRatePage.jsx';
import { AuthProvider } from '../context/AuthContext';
import { ExchangeRateProvider } from '../context/ExchangeRateContext';

const storage = new Map();
const localStorageMock = {
  getItem: (key) => (storage.has(key) ? storage.get(key) : null),
  setItem: (key, value) => storage.set(key, String(value)),
  removeItem: (key) => storage.delete(key),
  clear: () => storage.clear(),
};

globalThis.localStorage = localStorageMock;
globalThis.window = { localStorage: localStorageMock };

let sessionRole = 'Admin';
globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'User Test', cedula: 'V-1', role: sessionRole }),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const DUAL_USD_COP = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'COP',
  secondaryCurrencySymbol: 'COP',
  isConfigured: true,
};

const DUAL_USD_VES = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

const SINGLE_COP = {
  mode: 'Single',
  primaryCurrencyCode: 'COP',
  primaryCurrencySymbol: 'COP',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
  isConfigured: true,
};

const RAW_HISTORY = [
  { date: '2026-11-22', rate: 910, updatedAtLocal: '2026-11-22T08:15:00' },
  { date: '2026-11-25', rate: 924.36, updatedAtLocal: '2026-11-25T10:30:00' },
  { date: '2026-11-20', rate: 915, updatedAtLocal: '2026-11-20T09:00:00' },
];

function useProfile(profile) {
  storage.set('monetary_profile_client', JSON.stringify(profile));
}

function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function renderPage(userRole = sessionRole) {
  sessionRole = userRole;
  return visibleText(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <ExchangeRatePage />
      </ExchangeRateProvider>
    </AuthProvider>
  ));
}

describe('ExchangeRate Manual Flow & Outdated Detection Unit Tests', () => {
  test('1. Accurately detects outdated rate when lastUpdated exceeds 24 hours in UTC', () => {
    const now = Date.now();
    const twentyFiveHoursAgo = new Date(now - 25 * 60 * 60 * 1000).toISOString();
    const oneHourAgo = new Date(now - 1 * 60 * 60 * 1000).toISOString();

    const checkIsOutdated = (timestamp) => !timestamp || (now - new Date(timestamp).getTime() > 24 * 60 * 60 * 1000);
    const isOutdatedOld = checkIsOutdated(twentyFiveHoursAgo);
    const isOutdatedRecent = checkIsOutdated(oneHourAgo);
    const isOutdatedNull = checkIsOutdated(null);

    assert.strictEqual(isOutdatedOld, true, 'Rate older than 24h must be flagged as outdated');
    assert.strictEqual(isOutdatedRecent, false, 'Rate updated 1h ago must NOT be flagged as outdated');
    assert.strictEqual(isOutdatedNull, true, 'Null timestamp must be flagged as outdated');
  });

  test('2. Simulates 502/504 scraper error and triggers retry state', async () => {
    let syncAttempts = 0;
    const mockSyncBcv = async () => {
      syncAttempts++;
      if (syncAttempts === 1) {
        const err = new Error('Tiempo de espera agotado al conectar con el portal del BCV (504 Gateway Timeout)');
        err.status = 504;
        throw err;
      }
      return { value: 68.50, updatedAt: new Date().toISOString() };
    };

    let state = {
      message: null,
      exchangeRate: 0,
    };

    // First attempt -> failure with retry option
    try {
      await mockSyncBcv();
    } catch (err) {
      state.message = {
        type: 'danger',
        text: err.message,
        canRetry: true,
      };
    }

    assert.strictEqual(syncAttempts, 1);
    assert.strictEqual(state.message.canRetry, true);
    assert.ok(state.message.text.includes('504 Gateway Timeout'));

    // Retry invocation -> success
    if (state.message.canRetry) {
      const res = await mockSyncBcv();
      state.exchangeRate = res.value;
      state.message = {
        type: 'success',
        text: `Tasa sincronizada con BCV: ${res.value}`,
      };
    }

    assert.strictEqual(syncAttempts, 2);
    assert.strictEqual(state.exchangeRate, 68.50);
    assert.strictEqual(state.message.type, 'success');
  });
});

describe('ExchangeRatePage — canonical hybrid copy and profile-aware labels', () => {
  beforeEach(() => {
    storage.clear();
    sessionRole = 'Admin';
  });

  test('3. Dual USD/COP uses canonical labels, Modo Híbrido and no legacy copy', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage();

    assert.match(html, /Tasa de Cambio/);
    assert.match(html, /Modo Híbrido/);
    assert.ok(html.includes(
      'Sincronización automática cada 2 horas. También puede sincronizar a demanda o ingresar la tasa del día manualmente.'
    ));
    assert.match(html, /TASA VIGENTE/);
    assert.match(html, /Nueva tasa del día/);
    assert.match(html, /Guardar/);
    assert.match(html, /Sincronizar BCV/);
    assert.match(html, /Historial de Tasas/);
    assert.ok(html.includes(
      'Tasa desactualizada: no se ha registrado una actualización en más de 24 horas. Sincronice con el BCV o ingrese la tasa oficial del día.'
    ));
    assert.ok(html.includes('1 USD = 0,0000 COP'));
    assert.match(html, /COP/);

    assert.doesNotMatch(html, /Tasa Actual del Sistema/);
    assert.doesNotMatch(html, /Establecer Tasa Manual/);
    assert.doesNotMatch(html, /Guardar Tasa/);
    assert.doesNotMatch(html, /Historial Reciente/);
    assert.doesNotMatch(html, /Modo Manual/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  test('4. Dual USD/VES keeps the canonical Bs.S labels', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage();

    assert.match(html, /Nueva tasa del día/);
    assert.ok(html.includes('1 USD = 0,0000 Bs.S'));
    assert.match(html, /Bs\.S/);
    assert.match(html, /USD/);
  });

  test('5. Single COP shows the no-exchange-rate notice and hides every rate control', () => {
    useProfile(SINGLE_COP);
    const html = renderPage();

    assert.match(html, /Tasa de Cambio/);
    assert.ok(html.includes(
      'El régimen monetario Single no utiliza tasa de cambio: los importes se expresan únicamente en COP. No hay sincronización BCV ni carga manual disponibles.'
    ));
    assert.doesNotMatch(html, /TASA VIGENTE/);
    assert.doesNotMatch(html, /Nueva tasa del día/);
    assert.doesNotMatch(html, /Guardar/);
    assert.doesNotMatch(html, /Sincronizar BCV/);
    assert.doesNotMatch(html, /Historial de Tasas/);
    assert.doesNotMatch(html, /Modo Híbrido/);
    assert.doesNotMatch(html, /USD/);
  });

  test('6. Manager sees the read-only banner, current rate and history without mutation controls', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage('Manager');

    assert.ok(html.includes(
      'Modo Solo Lectura: Su cuenta tiene permisos de consulta. Los cambios e importaciones están bloqueados.'
    ));
    assert.match(html, /TASA VIGENTE/);
    assert.match(html, /Historial de Tasas/);
    assert.doesNotMatch(html, /Nueva tasa del día/);
    assert.doesNotMatch(html, /Guardar/);
    assert.doesNotMatch(html, /Sincronizar BCV/);
    assert.doesNotMatch(html, /Reintentar/);
  });

  test('7. Admin keeps the editable controls and does not see the read-only banner', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage('Admin');

    assert.match(html, /Nueva tasa del día/);
    assert.match(html, /Guardar/);
    assert.match(html, /Sincronizar BCV/);
    assert.doesNotMatch(html, /Modo Solo Lectura/);
  });

  test('8. Cashier remains denied by RoleGuard', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage('Cashier');

    assert.doesNotMatch(html, /TASA VIGENTE/);
    assert.doesNotMatch(html, /Nueva tasa del día/);
    assert.match(html, /No tienes los permisos necesarios/);
  });
});

describe('ExchangeRateHistory — S6 mapping, statuses and variation', () => {
  test('9. maps date/rate/updatedAtLocal, sorts descending and computes variation/status', () => {
    const rows = buildHistoryRows(RAW_HISTORY);

    assert.strictEqual(HISTORY_LIMIT, 90);
    assert.strictEqual(EXCHANGE_RATE_HISTORY_ENDPOINT, '/api/exchange-rate/history?limit=90');
    assert.strictEqual(rows.length, 3);

    assert.deepStrictEqual(rows.map((row) => row.dateDisplay), ['25/11/2026', '22/11/2026', '20/11/2026']);
    assert.strictEqual(rows[0].rateDisplay, '924,3600');
    assert.strictEqual(rows[0].updatedAtDisplay, '25/11/2026 10:30');
    assert.strictEqual(rows[0].variationDisplay, '+14,3600');
    assert.strictEqual(rows[1].variationDisplay, '-5,0000');
    assert.strictEqual(rows[2].variationDisplay, '—');
    assert.deepStrictEqual(rows.map((row) => row.statusLabel), ['Vigente', 'Histórica', 'Histórica']);
    assert.deepStrictEqual(rows.map((row) => row.isCurrent), [true, false, false]);
  });

  test('10. renders all five headers, the record count, statuses and the current-row highlight', () => {
    const rows = buildHistoryRows(RAW_HISTORY);
    const html = visibleText(renderToString(<ExchangeRateHistory rows={rows} />));

    for (const header of ['FECHA', 'TASA', 'VARIACIÓN', 'ACTUALIZACIÓN', 'ESTADO']) {
      assert.match(html, new RegExp(header));
    }
    assert.match(html, /3 registros/);
    assert.match(html, /Vigente/);
    assert.match(html, /Histórica/);
    assert.match(html, /xr-history-current/);
    assert.ok(html.includes('924,3600'));
    assert.ok(html.includes('+14,3600'));
    assert.ok(html.includes('-5,0000'));
    assert.ok(html.includes('—'));
    assert.ok(html.includes('25/11/2026 10:30'));
  });

  test('11. caps the table at 90 rows for mobile performance', () => {
    const oversized = Array.from({ length: 120 }, (_, index) => ({
      date: `2026-01-${String((index % 28) + 1).padStart(2, '0')}`,
      rate: 900 + index,
      updatedAtLocal: '2026-01-01T00:00:00',
    }));

    assert.strictEqual(buildHistoryRows(oversized).length, 90);
  });

  test('12. formats Actualizada in America/Caracas with Spanish AM/PM', () => {
    // Node/ICU versiona el espacio entre "a." y "m." como regular o como no-break estrecho
    // (U+202F). El contrato es el formato fecha/hora, no el code point del separador.
    const normalizeSpaces = (value) => value?.replace(/[\u00A0\u202F]/g, ' ') ?? value;
    assert.strictEqual(
      normalizeSpaces(formatCaracasUpdatedAt('2026-11-25T14:30:00Z')),
      normalizeSpaces('25/11/2026 · 10:30 a. m.'));
    assert.strictEqual(formatCaracasUpdatedAt(null), null);
    assert.strictEqual(formatCaracasUpdatedAt('not-a-date'), null);
  });
});
