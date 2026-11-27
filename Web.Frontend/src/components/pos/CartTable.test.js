// 8.211-T4b2b1 (S9): los encabezados y montos del carrito usan la etiqueta local del perfil
// (secundaria en Dual, primaria en Single) sin tocar la aritmética de líneas.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CartTable from './CartTable.jsx';
import { CartProvider } from '../../context/CartContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthProvider } from '../../context/AuthContext';

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
globalThis.sessionStorage = {
  getItem() {
    return null;
  },
  setItem() {},
  removeItem() {},
  clear() {},
};

function setProfile(profile) {
  profileStore.set('monetary_profile_client', JSON.stringify(profile));
}

function clearProfile() {
  profileStore.delete('monetary_profile_client');
}

const cartItem = {
  id: 1,
  productName: 'Harina PAN',
  quantity: 2,
  unitPrice: 10,
  unitPriceBsS: 874.5,
  subtotalBsS: 1749,
  isFractional: false,
  unitOfMeasure: 'Und',
};

function renderCartTable() {
  return renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CartProvider>
          <CartTable
            items={[cartItem]}
            selectedItemId={null}
            onSelectItem={() => {}}
            onUpdateQty={() => {}}
            onRemoveItem={() => {}}
          />
        </CartProvider>
      </ExchangeRateProvider>
    </AuthProvider>
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

describe('CartTable monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP uses COP in headers and amounts without a Bs.S hardcode', () => {
    setProfile(dualUsdCop);
    const html = renderCartTable();

    assert.match(html, /Precio COP/);
    assert.match(html, /Subtotal COP/);
    assert.match(html, /COP 874,50/);
    assert.match(html, /COP 1\.749,00/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Dual USD/VES keeps the Bs.S labels', () => {
    setProfile(dualUsdVes);
    const html = renderCartTable();

    assert.match(html, /Precio Bs\.S/);
    assert.match(html, /Subtotal Bs\.S/);
    assert.match(html, /Bs\.S 874,50/);
  });

  it('3. Single COP uses only the primary label', () => {
    setProfile(singleCop);
    const html = renderCartTable();

    assert.match(html, /Precio COP/);
    assert.match(html, /Subtotal COP/);
    assert.match(html, /COP 874,50/);
    assert.doesNotMatch(html, /Bs\.S/);
  });
});
