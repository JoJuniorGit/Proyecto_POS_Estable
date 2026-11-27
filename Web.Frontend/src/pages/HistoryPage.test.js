// 8.211-T4b2b2a (S9): las etiquetas del historial se resuelven desde el perfil activo. En Dual
// se muestran primaria y local; en Single sólo la primaria (sin tasa ni línea USD).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import HistoryPage, { resolveHistoryCurrencyLabels } from './HistoryPage.jsx';
import { AuthProvider } from '../context/AuthContext';
import { ExchangeRateProvider } from '../context/ExchangeRateContext';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'HistoryPage.jsx'), 'utf-8');

globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Cashier Test', cedula: 'V-1', role: 'Cashier' }),
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

describe('HistoryPage — perfil monetario [8.211-T4b2b2a]', () => {
  it('1. Dual USD/COP shows USD primary and COP local with the applied rate label', () => {
    const labels = resolveHistoryCurrencyLabels(DUAL_USD_COP);

    assert.strictEqual(labels.isDual, true);
    assert.strictEqual(labels.primaryCurrencyLabel, 'USD');
    assert.strictEqual(labels.localCurrencyLabel, 'COP');
    assert.strictEqual(labels.showPrimaryAmount, true);
    assert.strictEqual(labels.showRate, true);
    assert.strictEqual(labels.rateLabel, 'COP / USD');
  });

  it('2. Single COP collapses to the primary and hides the USD line and rate', () => {
    const labels = resolveHistoryCurrencyLabels(SINGLE_COP);

    assert.strictEqual(labels.isDual, false);
    assert.strictEqual(labels.primaryCurrencyLabel, 'COP');
    assert.strictEqual(labels.localCurrencyLabel, 'COP');
    assert.strictEqual(labels.showPrimaryAmount, false);
    assert.strictEqual(labels.showRate, false);
    assert.strictEqual(labels.rateLabel, null);
  });

  it('3. Dual USD/VES keeps the Bs.S presentation label', () => {
    const labels = resolveHistoryCurrencyLabels(DUAL_USD_VES);

    assert.strictEqual(labels.localCurrencyLabel, 'Bs.S');
    assert.strictEqual(labels.primaryCurrencyLabel, 'USD');
  });

  it('4. render paths no longer hardcode Total Bs.S / Total USD labels', () => {
    assert.doesNotMatch(source, /Total Bs\.S/);
    assert.doesNotMatch(source, /Total USD/);
    assert.match(source, /localCurrencyLabel/);
    assert.match(source, /primaryCurrencyLabel/);
    assert.match(source, /formatSecondary/);
    assert.match(source, /formatPrimary/);
  });

  it('5. mounts without throwing when the cached profile is Dual USD/COP', () => {
    const storage = new Map([['monetary_profile_client', JSON.stringify(DUAL_USD_COP)]]);
    globalThis.localStorage = {
      getItem: (key) => (storage.has(key) ? storage.get(key) : null),
      setItem: () => {},
      removeItem: () => {},
      clear: () => {},
    };
    globalThis.window = { localStorage: globalThis.localStorage };

    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <AuthProvider>
          <ExchangeRateProvider>
            <HistoryPage />
          </ExchangeRateProvider>
        </AuthProvider>
      );
    });
    assert.match(html, /Historial de Ventas/);
    assert.doesNotMatch(html, /Total Bs\.S/);
  });
});
