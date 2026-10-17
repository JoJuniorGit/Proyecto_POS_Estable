// 8.186-T4 (S2/S5): el Gran Total $ del carrito web muestra el USD EFECTIVO (server-first) que
// el servidor cobra en Bs.S, con el Bs.S exhibido intacto y sin alterar el totalUSD canónico.
// Montaje SSR real (renderToString, sin jsdom) con la venta activa inyectada por sessionStorage.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import SummaryPanel from './SummaryPanel.jsx';
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

function renderSummaryPanel(sale) {
  cachedSale = sale;
  return stripSsrComments(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CartProvider>
          <SummaryPanel onCheckout={() => {}} onHold={() => {}} />
        </CartProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  ));
}

describe('SummaryPanel Grand Total USD efectivo [8.186-T4]', () => {
  it('1. Renders the server effectiveTotalUSD (2dp display), not the canonical totalUSD', () => {
    const html = renderSummaryPanel({
      id: 21,
      status: 'Pending',
      items: [],
      subtotal: 6.3,
      totalUSD: 6.3,
      totalBsS: 315.35,
      appliedRate: 50,
      effectiveTotalUSD: 6.307,
    });

    assert.match(html, /\$ 6,31/);
    assert.doesNotMatch(html, /\$ 6,30/);
    assert.match(html, /Bs\.S 315,35/);
  });

  it('2. Falls back to the inverse of the effective Bs.S when the server field is absent', () => {
    const html = renderSummaryPanel({
      id: 22,
      status: 'Pending',
      items: [],
      subtotal: 33.4,
      totalUSD: 33.4,
      totalBsS: 100,
      appliedRate: 3,
    });

    assert.match(html, /\$ 33,33/);
    assert.doesNotMatch(html, /\$ 33,40/);
  });

  it('3. Keeps the canonical totalUSD when there is no effective Bs.S', () => {
    const html = renderSummaryPanel({
      id: 23,
      status: 'Pending',
      items: [],
      subtotal: 12.34,
      totalUSD: 12.34,
      totalBsS: 0,
      appliedRate: 0,
    });

    assert.match(html, /\$ 12,34/);
  });
});
