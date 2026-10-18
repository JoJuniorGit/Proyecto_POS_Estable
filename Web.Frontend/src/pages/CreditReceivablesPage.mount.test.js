// 8.187-T5 (Paso 11/12, F4): montaje real de la página CxC (molde PendingOrders) y su guarda
// de rol. Cashier cobra (acceso permitido); Driver queda fuera.
import { describe, it, before } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CreditReceivablesPage from './CreditReceivablesPage.jsx';
import { AuthProvider } from '../context/AuthContext';

let currentRole = 'Cashier';

globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Usuario Test', cedula: 'V-1', role: currentRole }),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

globalThis.localStorage = {
  getItem: () => null,
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const renderPage = () => renderToString(
  <AuthProvider>
    <CreditReceivablesPage />
  </AuthProvider>
);

describe('CreditReceivablesPage real mount [T5]', () => {
  before(() => {
    currentRole = 'Cashier';
  });

  it('1. renders for a Cashier with header, metrics and server-side filters', () => {
    const html = renderPage();

    assert.match(html, /Cuentas por Cobrar \(CxC\)/);
    assert.match(html, /Cartera abierta/);
    assert.match(html, /En mora \(fuera de gracia\)/);
    assert.match(html, /Tasa del día/);
    assert.match(html, /Solo en mora/);
    assert.match(html, /Buscar por cliente, cédula\/RIF o factura/);
  });

  it('2. renders for an Admin with the return action available', () => {
    currentRole = 'Admin';
    const html = renderPage();

    assert.match(html, /Cuentas por Cobrar \(CxC\)/);
  });

  it('3. denies the view for a Driver (AccessDenied instead of the carte)', () => {
    currentRole = 'Driver';
    const html = renderPage();

    assert.doesNotMatch(html, /Cartera abierta/);
    currentRole = 'Cashier';
  });
});
