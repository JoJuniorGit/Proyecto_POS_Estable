// 8.4-N5: prueba de MONTAJE REAL de CheckoutModal (renderToString de React).
// Detecta regresiones tipo 8.2-CR1 (identificadores libres como useRef sin importar),
// que un lint estático (oxlint) y los tests de módulo puro no detectan.
// No requiere jsdom: renderToString ejecuta el render inicial completo del componente
// y lanzaría ReferenceError si faltara un hook/import.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CheckoutModal from './CheckoutModal.jsx';
import { CartProvider } from '../../context/CartContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthProvider } from '../../context/AuthContext';

describe('CheckoutModal real mount [8.4-N5]', () => {
  it('renders to string inside providers without throwing (no free identifiers)', () => {
    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <AuthProvider>
          <ExchangeRateProvider>
            <CartProvider>
              <CheckoutModal isOpen onClose={() => {}} onSuccess={() => {}} />
            </CartProvider>
          </ExchangeRateProvider>
        </AuthProvider>
      );
    }, 'CheckoutModal must render without ReferenceError (regresión 8.2-CR1)');

    assert.ok(html.length > 0, 'rendered HTML must not be empty');
  });

  it('renders pending-orders checkout (overrideSale) without throwing', () => {
    assert.doesNotThrow(() => {
      renderToString(
        <AuthProvider>
          <ExchangeRateProvider>
            <CartProvider>
              <CheckoutModal
                isOpen
                onClose={() => {}}
                onCompleteSale={async () => 1}
                overrideSale={{
                  id: 42,
                  totalUSD: 100,
                  totalPaidUSD: 20,
                  remainingBalanceUSD: 80,
                  customerName: 'Cliente Test',
                  items: [],
                }}
              />
            </CartProvider>
          </ExchangeRateProvider>
        </AuthProvider>
      );
    });
  });
});