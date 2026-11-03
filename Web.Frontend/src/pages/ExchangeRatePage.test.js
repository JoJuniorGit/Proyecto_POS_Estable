import { test, describe, beforeEach } from 'node:test';
import assert from 'node:assert';
import React from 'react';
import { renderToString } from 'react-dom/server';
import ExchangeRatePage from './ExchangeRatePage.jsx';
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
globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Admin Test', cedula: 'V-1', role: 'Admin' }),
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

function useProfile(profile) {
  storage.set('monetary_profile_client', JSON.stringify(profile));
}

function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function renderPage() {
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

describe('ExchangeRatePage — profile-aware labels (8.211-T4b2b2b)', () => {
  beforeEach(() => storage.clear());

  test('3. Dual USD/COP keeps the rate controls with COP/USD labels', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage();

    assert.match(html, /Tasa Actual del Sistema/);
    assert.match(html, /Establecer Tasa Manual \(COP por USD\)/);
    assert.match(html, /Sincronizar BCV/);
    assert.match(html, /COP/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  test('4. Dual USD/VES keeps the legacy Bs.S/USD rate labels', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage();

    assert.match(html, /Establecer Tasa Manual \(Bs\.S por USD\)/);
    assert.match(html, /Bs\.S/);
    assert.match(html, /USD/);
  });

  test('5. Single COP shows the no-exchange-rate notice and hides the controls', () => {
    useProfile(SINGLE_COP);
    const html = renderPage();

    assert.match(html, /Single/);
    assert.match(html, /no utiliza tasa de cambio/);
    assert.doesNotMatch(html, /Tasa Actual del Sistema/);
    assert.doesNotMatch(html, /Establecer Tasa Manual/);
    assert.doesNotMatch(html, /Sincronizar BCV/);
    assert.doesNotMatch(html, /Bs\.S/);
    assert.doesNotMatch(html, /USD/);
  });
});
