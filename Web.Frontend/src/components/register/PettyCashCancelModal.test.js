// T5 (Paso 13 / 8.188, S5): modal de anulación de caja chica.
// Admin anula directo con motivo obligatorio; Manager/Cashier reciben el mensaje de autorización
// requerida (esta terminal web no completa el flujo de token en esta fase).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PettyCashCancelModal from './PettyCashCancelModal.jsx';

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
    React.createElement(PettyCashCancelModal, {
      isOpen: true,
      onClose: () => {},
      expense,
      role: 'Admin',
      onSuccess: () => {},
      ...props,
    })
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
  });

  it('2. blocks the direct cancel for Manager and Cashier with the authorization guidance', () => {
    for (const role of ['Manager', 'Cashier']) {
      const html = render({ role });

      assert.match(html, /requiere autorización/);
      assert.match(html, /WPF/);
      assert.doesNotMatch(html, /CONFIRMAR ANULACIÓN/);
    }
  });
});
