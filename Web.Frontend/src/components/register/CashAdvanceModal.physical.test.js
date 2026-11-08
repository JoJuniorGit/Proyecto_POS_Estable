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

const DUAL_USD_VES = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

function render(physicalMethodBalances) {
  return renderToString(
    React.createElement(CashAdvanceModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 1,
      availableCashBsS: 2000,
      exchangeRate: 50,
      user: { id: 1, name: 'Admin' },
      physicalMethodBalances,
      onSuccess: () => {},
    })
  );
}

describe('CashAdvanceModal - physical dispenser [8.212-WU5]', () => {
  beforeEach(() => {
    storage.clear();
    storage.set('monetary_profile_client', JSON.stringify(DUAL_USD_VES));
  });

  it('renders the sole physical method, its balance and the projection', () => {
    const html = render([
      {
        paymentMethodId: 10,
        name: 'Caja Bs.S',
        currencyCode: 'VES',
        currencySymbol: 'Bs.S',
        currentBalance: 500,
        isActive: true,
        canDispense: true,
      },
    ]);

    assert.match(html, /Método Físico que Entrega el Efectivo/);
    assert.match(html, /Caja Bs\.S/);
    assert.match(html, /Disponible:[\s\S]*Bs\.S 500,00/);
    assert.match(html, /Existencia actual: Bs\.S 500,00/);
  });

  it('shows the dispensed balance in the header instead of the legacy local cap', () => {
    const html = render([
      {
        paymentMethodId: 11,
        name: 'Caja USD',
        currencyCode: 'USD',
        currencySymbol: '$',
        currentBalance: 25,
        isActive: true,
        canDispense: true,
      },
    ]);

    assert.match(html, /Disponible en método físico:/);
    assert.match(html, /\$ 25,00/);
  });
});
