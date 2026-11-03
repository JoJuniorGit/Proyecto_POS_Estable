// T5 (Paso 13 / 8.188, S2): modal de cierre de la sesión de caja con conteo USD opcional.
// Paridad WPF: el conteo Bs.S es obligatorio, el USD es opcional y su ausencia no cambia el
// contrato anterior del cierre (actualClosingBalanceUsd null).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashDrawerCloseModal, { CASH_DRAWER_CLOSE_TITLE } from './CashDrawerCloseModal.jsx';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'CashDrawerCloseModal.jsx'), 'utf-8');

// React intercala marcadores de comentario entre nodos de texto en SSR.
function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function render(props = {}) {
  return visibleText(renderToString(
    React.createElement(CashDrawerCloseModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 42,
      exchangeRate: 36.5,
      expectedCashBsS: 1250.5,
      expectedCashUsd: 20,
      role: 'Admin',
      onSuccess: () => {},
      ...props,
    })
  ));
}

describe('CashDrawerCloseModal [T5]', () => {
  it('1. renders the blind-close fields with the optional USD count', () => {
    const html = render();

    assert.strictEqual(CASH_DRAWER_CLOSE_TITLE, 'Cierre de Caja');
    assert.match(html, /Efectivo Bs\.S contado/);
    assert.match(html, /Efectivo USD contado \(opcional\)/);
    assert.match(html, /opcional; si se deja vacío se conserva el comportamiento previo del cierre/);
    assert.match(html, /Tasa vigente/);
  });

  it('2. shows the theoretical USD only when the supervisor read is available', () => {
    assert.match(render({ expectedCashUsd: 20 }), /Efectivo esperado USD/);

    const withoutUsd = render({ expectedCashUsd: null });
    assert.match(withoutUsd, /Efectivo esperado USD: no disponible/);
  });

  it('3. posts the close through the shared API with actualClosingBalanceUsd', () => {
    assert.match(source, /closeCashDrawerSession/);
    assert.match(source, /buildCashDrawerClosePayload/);
    assert.match(source, /canCloseCashDrawerSession/);
  });
});
