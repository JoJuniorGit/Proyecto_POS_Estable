// T5 (Paso 13 / 8.188, S5) + residual 3: modal de anulación de caja chica.
// Admin anula directo con motivo obligatorio; Manager/Cashier ven el aviso de autorización y el
// mismo formulario: al confirmar se abre la espera supervisora y se reintenta con el token.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import { AuthorizationProvider } from '../authorization/AuthorizationProvider.jsx';
import PettyCashCancelModal from './PettyCashCancelModal.jsx';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'PettyCashCancelModal.jsx'), 'utf-8');

const expense = {
  id: 7,
  voucherNumber: 3,
  voucherCode: 'VCC-000003',
  category: 2,
  currency: 0,
  amountBsS: 1825,
  amountUsd: 50,
  concept: 'Cambio de bomba',
  beneficiary: 'Servicios Hidro',
  status: 0,
};

function render(props = {}) {
  return renderToString(
    React.createElement(
      AuthorizationProvider,
      null,
      React.createElement(PettyCashCancelModal, {
        isOpen: true,
        onClose: () => {},
        expense,
        role: 'Admin',
        onSuccess: () => {},
        ...props,
      })
    )
  );
}

describe('PettyCashCancelModal [T5]', () => {
  it('1. renders the expense summary and the mandatory reason field for an Admin', () => {
    const html = render();

    assert.match(html, /Anular Gasto de Caja Chica/);
    assert.match(html, /VCC-000003/);
    assert.match(html, /Cambio de bomba/);
    assert.match(html, /Servicios Hidro/);
    assert.match(html, /Motivo de la anulación/);
    assert.match(html, /CONFIRMAR ANULACIÓN/);
    assert.doesNotMatch(html, /espera de autorización/);
  });

  it('2. keeps the reason form for Manager and Cashier with the wait-flow notice instead of the WPF gate', () => {
    for (const role of ['Manager', 'Cashier']) {
      const html = render({ role });

      assert.match(html, /CONFIRMAR ANULACIÓN/);
      assert.match(html, /requiere autorización de un Administrador o Supervisor/);
      assert.match(html, /espera de autorización/);
      assert.doesNotMatch(html, /WPF/);
    }
  });

  it('3. composes the one-shot authorized retry through the petty cash authorization service', () => {
    assert.match(source, /useAuthorization/);
    assert.match(source, /startWait/);
    assert.match(source, /submitPettyCashWithAuthorization/);
    assert.match(source, /cancelPettyCashExpense/);
    assert.match(source, /getWebTerminalLabel/);
    assert.ok(
      !/while\s*\(.*cancelPettyCashExpense/.test(source),
      'no blind retry loop around the cancel call'
    );
  });
});
