// 8.211-T4b2b2a (S9): el adelanto de efectivo usa la moneda local del perfil, oculta la
// equivalencia USD en Single y replica la regla de efectivo del backend.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashAdvanceModal from './CashAdvanceModal.jsx';

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
    React.createElement(CashAdvanceModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 1,
      availableCashBsS: 2000,
      exchangeRate: 50,
      user: { id: 1, name: 'Admin' },
      onSuccess: () => {},
    })
  ));
}

describe('CashAdvanceModal — moneda local y precisión [8.211-T4b2b2a]', () => {
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

    assert.match(html, /Efectivo en Bs\.S - Solo Enteros\) \*/);
    assert.match(html, /step="1"/);
    assert.match(html, /inputMode="numeric"/);
  });

  it('2. Single COP accepts fractional cash, hides the USD equivalence and shows COP', () => {
    useProfile({
      mode: 'Single',
      primaryCurrencyCode: 'COP',
      primaryCurrencySymbol: 'COP',
      secondaryCurrencyCode: null,
      secondaryCurrencySymbol: null,
      isConfigured: true,
    });
    const html = render();

    assert.match(html, /Efectivo en COP\) \*/);
    assert.match(html, /step="0\.01"/);
    assert.match(html, /inputMode="decimal"/);
    assert.doesNotMatch(html, /Bs\.S/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
  });
});
