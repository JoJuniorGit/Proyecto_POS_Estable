import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import { AuthProvider } from '../context/AuthContext';
import { CurrencyFormatProvider } from '../context/CurrencyFormatContext';
import SettingsPage from './SettingsPage';

globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Admin', cedula: 'V-1', role: 'Admin' }),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

describe('SettingsPage modular shell', () => {
  it('renders the four existing domains and starts on currency', () => {
    const html = renderToString(
      <AuthProvider>
        <CurrencyFormatProvider>
          <SettingsPage />
        </CurrencyFormatProvider>
      </AuthProvider>
    );

    assert.match(html, /Moneda, Tasas y Formato/);
    assert.match(html, /Métodos de Pago y Caja/);
    assert.match(html, /Conectividad y Red Local/);
    assert.match(html, /Sistema y Mantenimiento/);
    assert.strictEqual((html.match(/role="tab"/g) || []).length, 4);
    assert.strictEqual((html.match(/aria-selected="true"/g) || []).length, 1);
    assert.match(html, /id="settings-panel-currency"/);
    assert.match(html, /id="settings-panel-payments"[^>]*hidden/);
    assert.match(html, /Moneda<\/th>/);
  });
});
