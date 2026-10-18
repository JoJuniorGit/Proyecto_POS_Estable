// T5 (Paso 13 / 8.188, S1/S4): modal de registro de caja chica.
// Moneda, monto, categoría, concepto, beneficiario y referencia; equivalencia dual en vivo con
// la tasa vigente (display; el servidor fija el snapshot) y aviso de autorización para el cajero.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PettyCashExpenseModal from './PettyCashExpenseModal.jsx';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'PettyCashExpenseModal.jsx'), 'utf-8');

function render(props = {}) {
  return renderToString(
    React.createElement(PettyCashExpenseModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 42,
      exchangeRate: 36.5,
      role: 'Admin',
      onSuccess: () => {},
      ...props,
    })
  );
}

describe('PettyCashExpenseModal [T5]', () => {
  it('1. renders currency options, typed categories and the contract fields', () => {
    const html = render();

    assert.match(html, /Registrar Gasto de Caja Chica/);
    assert.match(html, /Bs\.S \(bolívares\)/);
    assert.match(html, /USD \(divisas\)/);
    assert.match(html, /Insumos y Limpieza/);
    assert.match(html, /Servicios Públicos\/Eventuales/);
    assert.match(html, /Monto \(Bs\.S\)/);
    assert.match(html, /Concepto del gasto/);
    assert.match(html, /Beneficiario o proveedor/);
    assert.match(html, /Referencia del comprobante/);
  });

  it('2. shows the live dual equivalence at the current rate without fabricating it', () => {
    const html = render({ exchangeRate: 36.5 });

    assert.match(html, /Tasa vigente/);
    assert.match(html, /36,50/);
  });

  it('3. warns the Cashier that the web phase requires in-person supervisor authorization', () => {
    const cashierHtml = render({ role: 'Cashier' });

    assert.match(cashierHtml, /requiere autorización de un Administrador o Supervisor/);
    assert.match(cashierHtml, /WPF/);

    const adminHtml = render({ role: 'Admin' });
    assert.doesNotMatch(adminHtml, /desde una caja WPF/);
  });

  it('4. keeps the submit path single-shot with the idempotency key and the token header hook', () => {
    assert.match(source, /registerPettyCashExpense/);
    assert.match(source, /createCheckoutKeyHolder/);
    assert.match(source, /getOrCreateKey\(\)/);
    assert.match(source, /isPettyCashAuthorizationError/);
    assert.match(source, /buildPettyCashAuthorizationMessage/);
    assert.ok(
      !/while\s*\(.*registerPettyCashExpense/.test(source),
      'no blind retry loop around the register call'
    );
  });
});
