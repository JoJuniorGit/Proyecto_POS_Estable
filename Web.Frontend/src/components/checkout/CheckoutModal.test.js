import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CheckoutModal from './CheckoutModal.jsx';
import { createCheckoutKeyHolder } from '../../utils/idempotency.js';
import { CartProvider } from '../../context/CartContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthProvider } from '../../context/AuthContext';

const profileStore = new Map();
let cachedSale = null;

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
  getItem(key) {
    if (key === 'active_pos_sale_cache') {
      return cachedSale ? JSON.stringify(cachedSale) : null;
    }
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

const pendingSale = {
  id: 31,
  status: 'Pending',
  items: [],
  subtotal: 6.3,
  totalUSD: 6.3,
  totalBsS: 315.35,
  appliedRate: 50,
  effectiveTotalUSD: 7.5,
};

function renderCheckout() {
  cachedSale = pendingSale;
  return renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CartProvider>
          <CheckoutModal isOpen onClose={() => {}} onSuccess={() => {}} />
        </CartProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  ).replace(/<!-- -->/g, '');
}

describe('CheckoutModal Idempotency-Key Lifecycle [8.2-CR1 / 8.6-B4]', () => {
  it('1. Generates a stable key for the current checkout attempt (retries share the SAME key)', () => {
    const holder = createCheckoutKeyHolder();
    const keyAttempt1 = holder.getOrCreateKey();
    assert.ok(keyAttempt1 && keyAttempt1.length > 0, 'a key must be generated lazily');

    // Network retry of the SAME attempt -> MUST retain exact same key!
    const keyRetry = holder.getOrCreateKey();
    assert.strictEqual(keyRetry, keyAttempt1, 'Retrying same attempt must preserve Idempotency-Key');
  });

  it('2. Resets the key on discard/completion so a fresh attempt gets a brand new key', () => {
    const holder = createCheckoutKeyHolder();
    const keyAttempt1 = holder.getOrCreateKey();

    holder.reset();
    const keyAfterReset = holder.getOrCreateKey();
    assert.notStrictEqual(keyAfterReset, null, 'a fresh key must be generated after reset');
    assert.notStrictEqual(keyAfterReset, keyAttempt1, 'Fresh attempt must generate a new Idempotency-Key');
  });

  it('3. Uses crypto.randomUUID when available (RFC 4122 collision-free keys)', () => {
    let called = 0;
    const holder = createCheckoutKeyHolder(() => {
      called++;
      return '00000000-0000-4000-8000-000000000000';
    });
    assert.strictEqual(holder.getOrCreateKey(), '00000000-0000-4000-8000-000000000000');
    assert.strictEqual(called, 1, 'crypto UUID must be generated only once per attempt');
    holder.reset();
    holder.getOrCreateKey();
    assert.strictEqual(called, 2, 'after reset a NEW uuid must be generated');
  });
});

describe('CheckoutModal monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(() => {
    clearProfile();
    cachedSale = null;
  });

  it('1. Single COP shows the primary total and hides the USD reference line', () => {
    setProfile({
      mode: 'Single',
      primaryCurrencyCode: 'COP',
      primaryCurrencySymbol: 'COP',
      secondaryCurrencyCode: null,
      secondaryCurrencySymbol: null,
      isConfigured: true,
    });

    const html = renderCheckout();

    assert.match(html, /chk-font-total">COP 315,35/);
    assert.doesNotMatch(html, /Ref:/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Dual USD/COP keeps the COP total and the primary USD reference', () => {
    setProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'COP',
      secondaryCurrencySymbol: 'COP',
      isConfigured: true,
    });

    const html = renderCheckout();

    assert.match(html, /chk-font-total">COP 315,35/);
    assert.match(html, /Ref: \$ 7,50 USD/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('3. Dual USD/VES keeps Bs.S and the primary USD reference', () => {
    setProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
      isConfigured: true,
    });

    const html = renderCheckout();

    assert.match(html, /chk-font-total">Bs\.S 315,35/);
    assert.match(html, /Ref: \$ 7,50 USD/);
  });
});
