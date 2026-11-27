// 8.211-T4b2b2a (S9): la apertura de caja etiqueta el fondo local con el símbolo del perfil y
// oculta la segunda gaveta en Single.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashDrawerOpenModal from './CashDrawerOpenModal.jsx';

const storage = new Map();
const localStorageMock = {
  getItem: (key) => (storage.has(key) ? storage.get(key) : null),
  setItem: (key, value) => storage.set(key, String(value)),
  removeItem: (key) => storage.delete(key),
  clear: () => storage.clear(),
};

globalThis.localStorage = localStorageMock;
globalThis.window = { localStorage: localStorageMock };

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

const DUAL_USD_VES = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

function useProfile(profile) {
  storage.set('monetary_profile_client', JSON.stringify(profile));
}

function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function render() {
  return visibleText(renderToString(
    React.createElement(CashDrawerOpenModal, {
      isOpen: true,
      onClose: () => {},
      exchangeRate: 400,
      onSuccess: () => {},
    })
  ));
}

describe('CashDrawerOpenModal — perfil monetario [8.211-T4b2b2a]', () => {
  beforeEach(() => storage.clear());

  it('1. Dual USD/COP renders both drawers with COP and USD labels', () => {
    useProfile(DUAL_USD_COP);
    const html = render();

    assert.match(html, /Fondo inicial COP \*/);
    assert.match(html, /Fondo inicial USD/);
    assert.match(html, /Tasa aplicada: COP [\d.,]+ \/ USD/);
  });

  it('2. Single COP renders only the local drawer without USD or rate equivalence', () => {
    useProfile(SINGLE_COP);
    const html = render();

    assert.match(html, /Fondo inicial COP \*/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
    assert.doesNotMatch(html, /Tasa aplicada/);
  });

  it('3. Dual USD/VES keeps the Bs.S local drawer and the USD drawer', () => {
    useProfile(DUAL_USD_VES);
    const html = render();

    assert.match(html, /Fondo inicial Bs\.S \*/);
    assert.match(html, /Fondo inicial USD/);
  });
});
