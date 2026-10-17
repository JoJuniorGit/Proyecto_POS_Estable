// 8.186-T4 (S2/S5): el "Ref: $" del TOTAL del checkout web usa el USD EFECTIVO server-first:
// preview fresco (T3) → SaleDto del carrito → inversa local de display. En cuentas abiertas
// (overrideSale) el ítem mostrado es un saldo pendiente: se conserva la inversa local del Bs.S
// exhibido para no romper la identidad de display.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CheckoutModal, { resolveTotalEffectiveUsd } from './CheckoutModal.jsx';
import { CartProvider } from '../../context/CartContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthProvider } from '../../context/AuthContext';

let cachedSale = null;
globalThis.window = {};
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

function stripSsrComments(html) {
  return html.replace(/<!-- -->/g, '');
}

const baseDecision = {
  preview: null,
  isPreviewFresh: false,
  cartEffectiveTotalUSD: 0,
  overrideSale: null,
  targetTotalBsS: 100,
  targetTotalUSD: 33.4,
  rateToUse: 3,
};

describe('CheckoutModal total Ref USD efectivo [8.186-T4]', () => {
  it('1. Fresh preview effectiveTotalUSD wins over cart and local inverse', () => {
    const value = resolveTotalEffectiveUsd({
      ...baseDecision,
      preview: { effectiveTotalUSD: 33.3333 },
      isPreviewFresh: true,
      cartEffectiveTotalUSD: 99,
    });

    assert.strictEqual(value, 33.3333);
  });

  it('2. Stale preview is ignored; the cart SaleDto value is used', () => {
    const value = resolveTotalEffectiveUsd({
      ...baseDecision,
      preview: { effectiveTotalUSD: 99 },
      isPreviewFresh: false,
      cartEffectiveTotalUSD: 33.3333,
    });

    assert.strictEqual(value, 33.3333);
  });

  it('3. No preview -> cart SaleDto value wins over the local inverse', () => {
    const value = resolveTotalEffectiveUsd({
      ...baseDecision,
      preview: null,
      cartEffectiveTotalUSD: 33.3333,
    });

    assert.strictEqual(value, 33.3333);
  });

  it('4. No server value -> local display inverse of the Bs.S total (2dp)', () => {
    const value = resolveTotalEffectiveUsd({ ...baseDecision, cartEffectiveTotalUSD: 0 });

    assert.strictEqual(value, 33.33);
  });

  it('5. Open accounts (overrideSale) keep the local inverse of the displayed balance', () => {
    const value = resolveTotalEffectiveUsd({
      ...baseDecision,
      overrideSale: { id: 42 },
      preview: { effectiveTotalUSD: 100 },
      isPreviewFresh: true,
      cartEffectiveTotalUSD: 50,
    });

    assert.strictEqual(value, 33.33);
  });

  it('6. SSR render: the total row Ref uses the cart effectiveTotalUSD when no preview exists yet', () => {
    cachedSale = {
      id: 31,
      status: 'Pending',
      items: [],
      subtotal: 6.3,
      totalUSD: 6.3,
      totalBsS: 315.35,
      appliedRate: 50,
      effectiveTotalUSD: 7.5,
    };

    const html = stripSsrComments(renderToString(
      <AuthProvider>
        <ExchangeRateProvider>
          <CartProvider>
            <CheckoutModal isOpen onClose={() => {}} onSuccess={() => {}} />
          </CartProvider>
        </ExchangeRateProvider>
      </AuthProvider>
    ));

    assert.match(
      html,
      /chk-font-total">Bs\.S 315,35<\/div><div class="text-xs text-muted font-medium">Ref: \$7\.50 USD<\/div>/
    );
  });
});
