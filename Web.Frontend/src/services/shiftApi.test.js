import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  buildCloseShiftPayload,
  canViewUsdReconciliation,
  validateActualClosingBalanceUsd,
} from './shiftApi.js';

// Residual 8.187-8.188: payload del cierre diario con conteo USD opcional y gating de lectura.
describe('shiftApi - conteo USD del cierre diario', () => {
  it('omits actualClosingBalanceUsd when the count was not informed', () => {
    for (const raw of [undefined, null, '', '   ']) {
      const payload = buildCloseShiftPayload([{ paymentMethodId: 1, amount: 10 }], raw);
      assert.deepStrictEqual(payload, { declaredAmounts: [{ paymentMethodId: 1, amount: 10 }] });
      assert.strictEqual('actualClosingBalanceUsd' in payload, false);
    }
  });

  it('keeps an explicit zero count (zero is a real arqueo, not silence)', () => {
    const payload = buildCloseShiftPayload([], 0);
    assert.deepStrictEqual(payload, { declaredAmounts: [], actualClosingBalanceUsd: 0 });
  });

  it('sends the numeric count with four-decimal precision', () => {
    const payload = buildCloseShiftPayload([], '13.2500');
    assert.strictEqual(payload.actualClosingBalanceUsd, 13.25);
  });

  it('validates negative or non numeric counts and accepts blank as legacy behavior', () => {
    assert.strictEqual(validateActualClosingBalanceUsd(''), null);
    assert.strictEqual(validateActualClosingBalanceUsd('  '), null);
    assert.strictEqual(validateActualClosingBalanceUsd(undefined), null);
    assert.strictEqual(validateActualClosingBalanceUsd(0), null);
    assert.match(validateActualClosingBalanceUsd(-0.01), /USD/);
    assert.match(validateActualClosingBalanceUsd('abc'), /USD/);
  });

  it('gates expected/difference reading to Admin and Manager only', () => {
    assert.strictEqual(canViewUsdReconciliation('Admin'), true);
    assert.strictEqual(canViewUsdReconciliation('Manager'), true);
    assert.strictEqual(canViewUsdReconciliation('Cashier'), false);
    assert.strictEqual(canViewUsdReconciliation('Driver'), false);
    assert.strictEqual(canViewUsdReconciliation(null), false);
  });
});
