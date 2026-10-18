// T5 (Paso 13 / 8.188, F5): paridad web de caja chica en el registro de caja.
// La acción Caja Chica existe para Admin/Manager/Cashier con sesión activa; el cierre de la
// sesión (con conteo USD opcional) queda reservado a Admin/Manager; Driver sigue bloqueado.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import RegisterPage from './RegisterPage.jsx';
import { AuthProvider } from '../context/AuthContext';
import { ExchangeRateProvider } from '../context/ExchangeRateContext';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'RegisterPage.jsx'), 'utf-8');

let currentRole = 'Admin';

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

function renderPage() {
  return renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <RegisterPage />
      </ExchangeRateProvider>
    </AuthProvider>
  );
}

describe('RegisterPage — caja chica y cierre de sesión [T5]', () => {
  beforeEach(() => {
    currentRole = 'Admin';
  });

  it('1. renders the Caja Chica and Cerrar Caja actions for an Admin', () => {
    const html = renderPage();

    assert.match(html, /CAJA CHICA/);
    assert.match(html, /CERRAR CAJA/);
  });

  it('2. renders Caja Chica for a Cashier but not the session close action', () => {
    currentRole = 'Cashier';
    const html = renderPage();

    assert.match(html, /CAJA CHICA/);
    assert.doesNotMatch(html, /CERRAR CAJA/);
  });

  it('3. keeps the Driver out of the register view entirely', () => {
    currentRole = 'Driver';
    const html = renderPage();

    assert.match(html, /Acceso Denegado/);
    assert.doesNotMatch(html, /CAJA CHICA/);
  });

  it('4. wires the panel, both petty cash modals and the close modal through the service', () => {
    assert.match(source, /getPettyCashExpenses/);
    assert.match(source, /getPettyCashPhysicalBalances/);
    assert.match(source, /PettyCashPanel/);
    assert.match(source, /PettyCashExpenseModal/);
    assert.match(source, /PettyCashCancelModal/);
    assert.match(source, /CashDrawerCloseModal/);
    assert.match(source, /canUsePettyCash/);
    assert.match(source, /canCloseCashDrawerSession/);
  });
});
