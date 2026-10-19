// T5 (Paso 13 / 8.188, S1/S4) + residual 3: modal de registro de caja chica.
// Moneda, monto, categoría, concepto, beneficiario y referencia; equivalencia dual en vivo con la
// tasa vigente (display; el servidor fija el snapshot), aviso del flujo de espera para el cajero y
// composición del reintento autorizado (servicio de autorización de caja chica).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import { AuthorizationProvider } from '../authorization/AuthorizationProvider.jsx';
import PettyCashExpenseModal from './PettyCashExpenseModal.jsx';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'PettyCashExpenseModal.jsx'), 'utf-8');

function render(props = {}) {
  return renderToString(
    React.createElement(
      AuthorizationProvider,
      null,
      React.createElement(PettyCashExpenseModal, {
        isOpen: true,
        onClose: () => {},
        sessionId: 42,
        exchangeRate: 36.5,
        role: 'Admin',
        onSuccess: () => {},
        ...props,
      })
    )
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

  it('3. informs the Cashier about the supervisor wait flow without the obsolete WPF gate', () => {
    const cashierHtml = render({ role: 'Cashier' });

    assert.match(cashierHtml, /requiere autorización de un Administrador o Supervisor/);
    assert.match(cashierHtml, /espera de autorización/);
    assert.doesNotMatch(cashierHtml, /WPF/);

    const managerHtml = render({ role: 'Manager' });
    assert.doesNotMatch(managerHtml, /requiere autorización de un Administrador o Supervisor/);
  });

  it('4. composes the one-shot authorized retry through the petty cash authorization service', () => {
    assert.match(source, /useAuthorization/);
    assert.match(source, /startWait/);
    assert.match(source, /submitPettyCashWithAuthorization/);
    assert.match(source, /registerPettyCashExpense/);
    assert.match(source, /createCheckoutKeyHolder/);
    assert.match(source, /getOrCreateKey\(\)/);
    assert.match(source, /getIdempotencyKey/);
    assert.match(source, /getWebTerminalLabel/);
    assert.ok(
      !/while\s*\(.*registerPettyCashExpense/.test(source),
      'no blind retry loop around the register call'
    );
  });
});
