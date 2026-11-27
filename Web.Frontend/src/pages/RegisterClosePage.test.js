// 8.211-T4b2b2a (S9): el cierre ciego usa el perfil para la tasa, el conteo opcional USD y los
// badges de moneda. En Single no se ofrece ni muestra una segunda gaveta.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import RegisterClosePage from './RegisterClosePage.jsx';
import { AuthProvider } from '../context/AuthContext';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'RegisterClosePage.jsx'), 'utf-8');

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
      <RegisterClosePage />
    </AuthProvider>
  ));
}

describe('RegisterClosePage — perfil monetario [8.211-T4b2b2a]', () => {
  beforeEach(() => storage.clear());

  it('1. Dual USD/COP labels the informative rate with COP and USD', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage();

    assert.match(html, /Cierre Ciego de Caja/);
    assert.match(html, /Tasa del Día \(Informativa\):/);
    assert.match(html, /COP [\d.,]+/);
    assert.match(html, /\/ USD/);
  });

  it('2. Single COP hides the rate line and the optional USD count', () => {
    useProfile(SINGLE_COP);
    const html = renderPage();

    assert.match(html, /Cierre Ciego de Caja/);
    assert.doesNotMatch(html, /Tasa del Día/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
  });

  it('3. method labels and badges resolve from the persisted currency code', () => {
    assert.match(source, /normalizeCurrencyCode\(method\?\.currency\)/);
    assert.match(source, /displayCurrencyCode\(currency\)/);
    assert.match(source, /symbolFor\(currency\)/);
    assert.ok(!/\{isUsd \? 'USD' : 'Bs\.S'\}/.test(source));
    assert.ok(!/prefix=\{isUsd \? '\$' : 'Bs\.S'\}/.test(source));
  });
});
