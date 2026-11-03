// 8.211-T4b2b2a (S9): las tarjetas, la tasa y la tabla de movimientos de caja usan el perfil
// activo. En Single se oculta la equivalencia USD y la tasa; en Dual se mantiene la primaria USD.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import RegisterPage from './RegisterPage.jsx';
import { AuthProvider } from '../context/AuthContext';
import { ExchangeRateProvider } from '../context/ExchangeRateContext';
import { AuthorizationProvider } from '../components/authorization/AuthorizationProvider.jsx';

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
      <ExchangeRateProvider>
        <AuthorizationProvider>
          <RegisterPage />
        </AuthorizationProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  ));
}

describe('RegisterPage — perfil monetario [8.211-T4b2b2a]', () => {
  beforeEach(() => storage.clear());

  it('1. Dual USD/COP keeps the USD primary and labels the local drawer COP', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage();

    assert.match(html, /EFECTIVO ESPERADO EN CAJA/);
    assert.match(html, /COP/);
    assert.match(html, /Tasa Actual: COP [\d.,]+ \/ USD/);
  });

  it('2. Single COP hides the USD equivalence, the USD column and the rate', () => {
    useProfile(SINGLE_COP);
    const html = renderPage();

    assert.match(html, /COP/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
    assert.doesNotMatch(html, /Tasa Actual/);
  });

  it('3. Dual USD/VES keeps Bs.S and USD labels', () => {
    useProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
      isConfigured: true,
    });
    const html = renderPage();

    assert.match(html, /Bs\.S/);
    assert.match(html, /USD/);
  });
});
