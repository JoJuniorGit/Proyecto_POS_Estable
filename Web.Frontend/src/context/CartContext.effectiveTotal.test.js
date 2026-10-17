// 8.186-T4 (S2/S5): USD EFECTIVO del carrito web — server-first, sin política de redondeo en JS.
// Cubre el resolver exportado (server > inversa 4dp > canónico) y el estado real del provider
// (montaje SSR sin jsdom, inyectando la venta activa por sessionStorage como al restaurar pestaña).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import { CartProvider, useCartState, resolveEffectiveTotalUSD } from './CartContext.jsx';
import { ExchangeRateProvider } from './ExchangeRateContext';
import { AuthProvider } from './AuthContext';

// El provider lee la caché de la venta activa de sessionStorage al montar (contrato real de
// restauración). El probe expone el estado del contexto para verificar la superficie pública.
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

function CartTotalsProbe() {
  const { totalUSD, totalBsS, effectiveTotalUSD } = useCartState();
  return <span id="cart-totals">{`${totalUSD}|${totalBsS}|${effectiveTotalUSD}`}</span>;
}

function renderCartProbe(sale) {
  cachedSale = sale;
  return renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CartProvider>
          <CartTotalsProbe />
        </CartProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  );
}

describe('CartContext effectiveTotalUSD [8.186-T4]', () => {
  it('1. Server-sent effectiveTotalUSD wins over any local derivation (server-first)', () => {
    const value = resolveEffectiveTotalUSD({ effectiveTotalUSD: 7.5 }, 315.35, 50, 6.3);
    assert.strictEqual(value, 7.5);
  });

  it('2. Inverse fallback at 4dp (accounting) when the server field is absent or zero', () => {
    assert.strictEqual(resolveEffectiveTotalUSD({}, 100, 3, 33.4), 33.3333);
    assert.strictEqual(resolveEffectiveTotalUSD({ effectiveTotalUSD: 0 }, 100, 3, 33.4), 33.3333);
  });

  it('3. Canonical totalUSD fallback when there is no rate or no effective Bs.S', () => {
    assert.strictEqual(resolveEffectiveTotalUSD({}, 100, 0, 12.34), 12.34);
    assert.strictEqual(resolveEffectiveTotalUSD({}, 0, 50, 12.34), 12.34);
    assert.strictEqual(resolveEffectiveTotalUSD({}, 0, 0, 12.34), 12.34);
  });

  it('4. Provider exposes the server value and keeps totalUSD canonical', () => {
    const html = renderCartProbe({
      id: 11,
      status: 'Pending',
      items: [],
      subtotal: 6.3,
      totalUSD: 6.3,
      totalBsS: 315.35,
      appliedRate: 50,
      effectiveTotalUSD: 6.307,
    });

    assert.match(html, /6\.3\|315\.35\|6\.307/);
  });

  it('5. Provider falls back to the 4dp inverse when the cached sale lacks the server field', () => {
    const html = renderCartProbe({
      id: 12,
      status: 'Pending',
      items: [],
      subtotal: 33.4,
      totalUSD: 33.4,
      totalBsS: 100,
      appliedRate: 3,
    });

    assert.match(html, /33\.4\|100\|33\.3333/);
  });

  it('6. Provider falls back to canonical totalUSD when there is no effective Bs.S', () => {
    const html = renderCartProbe({
      id: 13,
      status: 'Pending',
      items: [],
      subtotal: 12.34,
      totalUSD: 12.34,
      totalBsS: 0,
      appliedRate: 0,
    });

    assert.match(html, /12\.34\|0\|12\.34/);
  });
});
