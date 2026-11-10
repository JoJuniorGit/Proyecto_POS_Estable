// 8.211-T4b2b1 (S9): la lista de pagos aplicados conserva primaria + secundaria en Dual y
// colapsa a una sola línea en Single (sin duplicar el mismo monto con otro símbolo).
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PaymentList from './PaymentList.jsx';

const profileStore = new Map();
globalThis.window = {
  localStorage: {
    getItem(key) {
      return profileStore.has(key) ? profileStore.get(key) : null;
    },
    setItem(key, value) {
      profileStore.set(key, value);
    },
    removeItem(key) {
      profileStore.delete(key);
    },
  },
};

function setProfile(profile) {
  profileStore.set('monetary_profile_client', JSON.stringify(profile));
}

function clearProfile() {
  profileStore.delete('monetary_profile_client');
}

function renderPaymentList() {
  return renderToString(
    <PaymentList
      payments={[{ uid: 'p-1', methodName: 'Efectivo', amountBsS: 100, amountUsd: 4 }]}
      onRemovePayment={() => {}}
    />
  ).replace(/<!-- -->/g, '');
}

const dualUsdCop = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'COP',
  secondaryCurrencySymbol: 'COP',
  isConfigured: true,
};

const dualUsdVes = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

const singleCop = {
  mode: 'Single',
  primaryCurrencyCode: 'COP',
  primaryCurrencySymbol: 'COP',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
  isConfigured: true,
};

describe('PaymentList monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP keeps both the COP and primary USD lines', () => {
    setProfile(dualUsdCop);
    const html = renderPaymentList();

    assert.match(html, /payment-item-bss">COP 100,00/);
    assert.match(html, /payment-item-usd[^>]*>\(\$ 4,00\)/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Dual USD/VES keeps the Bs.S line and the USD reference', () => {
    setProfile(dualUsdVes);
    const html = renderPaymentList();

    assert.match(html, /Bs\.S 100,00/);
    assert.match(html, /\(\$ 4,00\)/);
  });

  it('3. Single COP hides the duplicate primary/secondary line', () => {
    setProfile(singleCop);
    const html = renderPaymentList();

    assert.match(html, /COP 100,00/);
    assert.doesNotMatch(html, /payment-item-usd/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /Bs\.S/);
  });
});
