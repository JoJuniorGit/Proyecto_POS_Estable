import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import HistoryPage from './HistoryPage.jsx';
import { ExchangeRateProvider } from '../context/ExchangeRateContext';

describe('HistoryPage real mount [8.108]', () => {
  it('renders to string inside ExchangeRateProvider without throwing', () => {
    let html = '';
    assert.doesNotThrow(() => {
      html = renderToString(
        <ExchangeRateProvider>
          <HistoryPage />
        </ExchangeRateProvider>
      );
    });
    assert.ok(html.length > 0, 'rendered HTML must not be empty');
  });

  it('renders the cashier filter and test-sales toggle controls', () => {
    const html = renderToString(
      <ExchangeRateProvider>
        <HistoryPage />
      </ExchangeRateProvider>
    );
    assert.match(html, /Cajero \(buscador por usuario\)/);
    assert.match(html, /Ocultar transacciones de prueba/);
    assert.doesNotMatch(html, /BOT_STRESS_TEST.*checked/);
  });
});