import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashInModal from './CashInModal.jsx';
import CashOutModal from './CashOutModal.jsx';

/**
 * 8.149 (SRE-02/D4): POST /api/cashdrawer/transaction exige Idempotency-Key en el backend.
 * El runner web no dispone de DOM (react-dom/test-utils o jsdom), por lo que la asercion
 * mas fuerte factible es anclar por fuente el wiring del header en el call site real
 * (mismo estilo que CashAdvanceModal.commission.test.js) y confirmar el montaje del
 * componente con renderToString. La semantica del holder estable por intento ya esta
 * cubierta por CheckoutModal.test.js y salesApi.hold.test.js.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const readSource = (name) => fs.readFileSync(path.resolve(__dirname, name), 'utf-8');

const cashInSource = readSource('CashInModal.jsx');
const cashOutSource = readSource('CashOutModal.jsx');

function assertTransactionKeyWiring(source, label) {
  assert.ok(
    /api\.post\(\s*['"]\/api\/cashdrawer\/transaction['"]/.test(source),
    `${label}: debe postear a /api/cashdrawer/transaction`
  );
  assert.ok(
    /api\.post\(\s*['"]\/api\/cashdrawer\/transaction['"][\s\S]{0,500}?headers:\s*\{\s*['"]Idempotency-Key['"]/.test(source),
    `${label}: el POST de transaccion debe enviar el header Idempotency-Key`
  );
  assert.ok(
    /createCheckoutKeyHolder\(\)/.test(source) && /getOrCreateKey\(\)/.test(source),
    `${label}: la clave debe salir del holder estable por intento (utils/idempotency)`
  );
  assert.ok(
    /cashKeyHolderRef\.current\.reset\(\)/.test(source),
    `${label}: el holder debe resetearse al cerrar/exito para que la proxima transaccion estrene clave`
  );
}

describe('cash transaction modals send Idempotency-Key (8.149/SRE-02)', () => {
  it('1. CashInModal wires a stable-per-attempt key on the transaction POST', () => {
    assertTransactionKeyWiring(cashInSource, 'CashInModal');
  });

  it('2. CashOutModal wires a stable-per-attempt key on the transaction POST', () => {
    assertTransactionKeyWiring(cashOutSource, 'CashOutModal');
  });

  it('3. Both modals mount without throwing', () => {
    const cases = [
      ['CashInModal', CashInModal],
      ['CashOutModal', CashOutModal],
    ];

    for (const [label, Component] of cases) {
      let html = '';
      assert.doesNotThrow(() => {
        html = renderToString(
          React.createElement(Component, {
            isOpen: true,
            onClose: () => {},
            sessionId: 5,
            exchangeRate: 40,
            user: { name: 'Admin' },
            onSuccess: () => {},
          })
        );
      }, `${label} must render without ReferenceError`);
      assert.ok(html.length > 0, `${label} rendered HTML must not be empty`);
    }
  });
});
