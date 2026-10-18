// 8.186-T7b (S2 ext. cuentas abiertas): el resumen del modal de edición ancla TODA su matemática
// a la tasa de la venta (`sale.appliedRate`, la que el server persiste al editar los ítems y la
// que usa el POS local) y muestra el Bs.S REAL ya abonado (`totalPaidBsS`), no una síntesis
// USD × tasa vigente. Escenario: la tasa BCV cambió (50 → 60) después de abrir la cuenta; con la
// tasa vigente la fila "Total Ya Abonado" mostraría 1.200 sobre abonos que fueron 1.000 y las
// tres filas del resumen dejarían de reconciliar.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import EditSaleModal, {
  computeEditSaleSummary,
  resolveAccountRate,
  resolveUnitPriceBsS,
} from './EditSaleModal.jsx';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';

globalThis.window = {};

function stripSsrComments(html) {
  return html.replace(/<!-- -->/g, '');
}

const APPLIED_RATE = 50; // tasa anclada de la venta (la que el server persiste al editar)
const LIVE_RATE = 60; // tasa vigente posterior al cambio BCV

const sale = {
  id: 42,
  status: 'Pending',
  appliedRate: APPLIED_RATE,
  totalPaidUSD: 20,
  payments: [{ amount: 20, amountBsS: 1000, exchangeRate: APPLIED_RATE }],
};

const items = [
  {
    productId: 10,
    productName: 'Harina PAN',
    quantity: 2,
    unitPrice: 15,
    unitPriceBsS: 1500,
    isFractional: false,
  },
];

describe('EditSaleModal — tasa anclada y Bs.S real de abonos [8.186-T7b]', () => {
  it('1. "Total Ya Abonado" usa el Bs.S REAL abonado (1.000), no USD × tasa vigente (1.200)', () => {
    const summary = computeEditSaleSummary({ items, sale, exchangeRate: LIVE_RATE });

    assert.strictEqual(summary.totalPaidBsS, 1000);
    assert.strictEqual(summary.totalPaidUSD, 20);
    assert.notStrictEqual(summary.totalPaidBsS, sale.totalPaidUSD * LIVE_RATE);
  });

  it('2. Las tres filas reconcilian: newTotalBsS == totalPaidBsS + restante', () => {
    const summary = computeEditSaleSummary({ items, sale, exchangeRate: LIVE_RATE });

    assert.strictEqual(summary.newTotalBsS, 3000);
    assert.strictEqual(summary.newRemainingBalanceBsS, Math.max(0, summary.newTotalBsS - summary.totalPaidBsS));
    assert.strictEqual(summary.newTotalBsS, summary.totalPaidBsS + summary.newRemainingBalanceBsS);
  });

  it('3. El restante se clampa en 0 cuando los abonos superan el nuevo total', () => {
    const overpaid = computeEditSaleSummary({
      items: [{ quantity: 1, unitPrice: 5, unitPriceBsS: 250 }],
      sale,
      exchangeRate: LIVE_RATE,
    });

    assert.strictEqual(overpaid.newRemainingBalanceBsS, 0);
    assert.ok(overpaid.newTotalBsS < overpaid.totalPaidBsS);
  });

  it('4. Inversas del resumen a tasa anclada (3000/50=60 y 2000/50=40; la live daría 50 y 33,33)', () => {
    const summary = computeEditSaleSummary({ items, sale, exchangeRate: LIVE_RATE });

    assert.strictEqual(summary.newTotalEffectiveUsd, 60);
    assert.strictEqual(summary.newRemainingEffectiveUsd, 40);
  });

  it('5. Abono sin amountBsS cae a su exchangeRate y luego a la tasa anclada (no a la live)', () => {
    const withoutBsS = computeEditSaleSummary({
      items,
      sale: { appliedRate: APPLIED_RATE, totalPaidUSD: 20, payments: [{ amount: 20 }] },
      exchangeRate: LIVE_RATE,
    });
    assert.strictEqual(withoutBsS.totalPaidBsS, 1000);

    const withOwnRate = computeEditSaleSummary({
      items,
      sale: { appliedRate: APPLIED_RATE, totalPaidUSD: 20, payments: [{ amount: 20, exchangeRate: 52 }] },
      exchangeRate: LIVE_RATE,
    });
    assert.strictEqual(withOwnRate.totalPaidBsS, 1040);
  });

  it('6. unitPriceBsS fallback (ítem nuevo) usa techo a tasa anclada: 1,5 × 50 = 75, no 90', () => {
    assert.strictEqual(resolveUnitPriceBsS(0, 1.5, APPLIED_RATE), 75);
    assert.strictEqual(resolveUnitPriceBsS(0, 1.5, LIVE_RATE), 90);
    assert.strictEqual(resolveUnitPriceBsS(1500, 15, LIVE_RATE), 1500);

    const newItem = computeEditSaleSummary({
      items: [{ quantity: 1, unitPrice: 1.5 }],
      sale: { appliedRate: APPLIED_RATE, payments: [] },
      exchangeRate: LIVE_RATE,
    });
    assert.strictEqual(newItem.newTotalBsS, 75);
  });

  it('7. resolveAccountRate prioriza la tasa anclada y cae a la vigente solo sin ancla', () => {
    assert.strictEqual(resolveAccountRate({ appliedRate: APPLIED_RATE }, LIVE_RATE), APPLIED_RATE);
    assert.strictEqual(resolveAccountRate({ appliedRate: 0 }, LIVE_RATE), LIVE_RATE);
    assert.strictEqual(resolveAccountRate({ appliedRate: -50 }, LIVE_RATE), LIVE_RATE);
    assert.strictEqual(resolveAccountRate({}, LIVE_RATE), LIVE_RATE);
    assert.strictEqual(resolveAccountRate(null, LIVE_RATE), LIVE_RATE);
  });

  it('8. Render: "Total Ya Abonado" muestra Bs.S 1.000,00 ($ 20,00) y nunca Bs.S 1.200,00', () => {
    const html = stripSsrComments(
      renderToString(
        <ExchangeRateProvider>
          <EditSaleModal
            isOpen
            onClose={() => {}}
            sale={sale}
            exchangeRate={LIVE_RATE}
            onSuccess={() => {}}
          />
        </ExchangeRateProvider>
      )
    );

    assert.match(html, /Bs\.S 1\.000,00/);
    assert.match(html, /\(\$ 20,00\)/);
    assert.doesNotMatch(html, /Bs\.S 1\.200,00/);
  });

  it('9. Venta sin tasa anclada: cae a la vigente (comportamiento legado intacto)', () => {
    const legacy = computeEditSaleSummary({
      items,
      sale: { totalPaidUSD: 20, payments: [{ amount: 20 }] },
      exchangeRate: LIVE_RATE,
    });

    assert.strictEqual(legacy.accountRate, LIVE_RATE);
    assert.strictEqual(legacy.totalPaidBsS, 1200);
    assert.strictEqual(legacy.newTotalEffectiveUsd, 50);
    assert.strictEqual(legacy.newTotalBsS, legacy.totalPaidBsS + legacy.newRemainingBalanceBsS);
  });
});
