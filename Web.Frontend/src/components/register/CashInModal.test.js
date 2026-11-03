// 8.211-T4b2b2a (S9): CASH IN usa la moneda local del perfil y replica la regla de efectivo del
// backend (Dual USD/VES entero; Single y Dual no-VES con centavos).
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashInModal from './CashInModal.jsx';

const storage = new Map();
const localStorageMock = {
  getItem: (key) => (storage.has(key) ? storage.get(key) : null),
  setItem: (key, value) => storage.set(key, String(value)),
  removeItem: (key) => storage.delete(key),
  clear: () => storage.clear(),
};

globalThis.localStorage = localStorageMock;
globalThis.window = { localStorage: localStorageMock };

function useProfile(profile) {
  storage.set('monetary_profile_client', JSON.stringify(profile));
}

function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function render() {
  return visibleText(renderToString(
    React.createElement(CashInModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 1,
      exchangeRate: 50,
      user: { id: 1, name: 'Admin' },
      onSuccess: () => {},
    })
  ));
}

describe('CashInModal — moneda local y precisión [8.211-T4b2b2a]', () => {
  beforeEach(() => storage.clear());

  it('1. Dual USD/VES keeps the integer-only Bs.S input', () => {
    useProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
      isConfigured: true,
    });
    const html = render();

    assert.match(html, /Monto a Ingresar \(Bs\.S - Solo Enteros\) \*/);
    assert.match(html, /step="1"/);
    assert.match(html, /inputMode="numeric"/);
  });

  it('2. Single COP accepts fractional cash and shows only the COP label', () => {
    useProfile({
      mode: 'Single',
      primaryCurrencyCode: 'COP',
      primaryCurrencySymbol: 'COP',
      secondaryCurrencyCode: null,
      secondaryCurrencySymbol: null,
      isConfigured: true,
    });
    const html = render();

    assert.match(html, /Monto a Ingresar \(COP\) \*/);
    assert.match(html, /step="0\.01"/);
    assert.match(html, /inputMode="decimal"/);
    assert.doesNotMatch(html, /Bs\.S/);
    assert.doesNotMatch(html, /USD/);
  });
});
