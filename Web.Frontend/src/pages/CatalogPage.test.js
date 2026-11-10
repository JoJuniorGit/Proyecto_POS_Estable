// 8.211-T4b2b2b (S9): el filtro de moneda del catálogo arranca en la moneda local del perfil
// (secundaria en `Dual`, primaria en `Single`) y ofrece únicamente las monedas del perfil. Sin
// aritmética nueva: los precios se formatean con formatPrimary/formatSecondary.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CatalogPage, {
  buildCatalogCurrencyOptions,
  resolveCatalogCurrencySelection,
} from './CatalogPage.jsx';
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

function renderCatalog() {
  return visibleText(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CatalogPage />
      </ExchangeRateProvider>
    </AuthProvider>
  ));
}

describe('8.211-T4b2b2b — catalog currency filter', () => {
  beforeEach(() => storage.clear());

  it('1. builds the primary/secondary options and defaults to the local currency', () => {
    assert.deepStrictEqual(buildCatalogCurrencyOptions(DUAL_USD_COP), [
      { value: 'USD', label: 'USD' },
      { value: 'COP', label: 'COP' },
    ]);
    assert.strictEqual(resolveCatalogCurrencySelection(DUAL_USD_COP), 'COP');
    assert.strictEqual(resolveCatalogCurrencySelection(SINGLE_COP), 'COP');
    assert.deepStrictEqual(buildCatalogCurrencyOptions(DUAL_USD_VES), [
      { value: 'USD', label: 'USD' },
      { value: 'VES', label: 'Bs.S' },
    ]);
  });

  it('2. renders the Dual USD/COP options and selects the COP local currency', () => {
    useProfile(DUAL_USD_COP);
    const html = renderCatalog();

    assert.match(html, /<option value="USD">USD<\/option>/);
    assert.match(html, /<option value="COP" selected="">COP<\/option>/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('3. renders only the primary option in Single COP', () => {
    useProfile(SINGLE_COP);
    const html = renderCatalog();

    assert.match(html, /<option value="COP" selected="">COP<\/option>/);
    assert.doesNotMatch(html, /value="USD"/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('4. keeps the USD/Bs.S options in Dual USD/VES', () => {
    useProfile(DUAL_USD_VES);
    const html = renderCatalog();

    assert.match(html, /<option value="USD">USD<\/option>/);
    assert.match(html, /<option value="VES" selected="">Bs\.S<\/option>/);
  });
});
