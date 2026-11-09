import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  buildPhysicalCurrencySummaries,
  sortPhysicalMethodBalances,
} from './registerFormLogic.js';

const DUAL_USD_VES = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
};

const SINGLE_EUR = {
  mode: 'Single',
  primaryCurrencyCode: 'EUR',
  primaryCurrencySymbol: '€',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
};

describe('physical currency summaries', () => {
  it('puts the local currency first and aggregates all its methods', () => {
    const summary = buildPhysicalCurrencySummaries(DUAL_USD_VES, [
      { paymentMethodId: 1, name: 'Efectivo USD', currencyCode: 'USD', currentBalance: 10 },
      { paymentMethodId: 2, name: 'Efectivo Bs.S', currencyCode: 'VES', currentBalance: 100 },
      { paymentMethodId: 3, name: 'Caja Bs.S', currencyCode: 'VES', currentBalance: 50 },
    ]);

    assert.deepStrictEqual(summary.map((row) => row.code), ['VES', 'USD']);
    assert.strictEqual(summary[0].isLocal, true);
    assert.strictEqual(summary[0].total, 150);
    assert.strictEqual(summary[1].total, 10);
  });

  it('shows a zero balance for a profile currency without a physical method', () => {
    const summary = buildPhysicalCurrencySummaries(DUAL_USD_VES, [
      { paymentMethodId: 1, name: 'Efectivo USD', currencyCode: 'USD', currentBalance: 25 },
    ]);

    assert.deepStrictEqual(summary.map((row) => row.code), ['VES', 'USD']);
    assert.strictEqual(summary[0].total, 0);
    assert.strictEqual(summary[1].total, 25);
  });

  it('keeps legacy extra currencies after the local one in Single mode', () => {
    const summary = buildPhysicalCurrencySummaries(SINGLE_EUR, [
      { paymentMethodId: 1, name: 'Caja USD', currencyCode: 'USD', currentBalance: 5 },
      { paymentMethodId: 2, name: 'Caja EUR', currencyCode: 'EUR', currentBalance: 20 },
    ]);

    assert.deepStrictEqual(summary.map((row) => row.code), ['EUR', 'USD']);
    assert.strictEqual(summary[0].isLocal, true);
    assert.strictEqual(summary[0].total, 20);
    assert.strictEqual(summary[1].total, 5);
  });

  it('normalizes VES aliases when aggregating', () => {
    const summary = buildPhysicalCurrencySummaries(DUAL_USD_VES, [
      { paymentMethodId: 1, name: 'Caja Bs.S', currencyCode: 'Bs.S', currentBalance: 40 },
    ]);

    assert.strictEqual(summary[0].code, 'VES');
    assert.strictEqual(summary[0].total, 40);
  });

  it('sorts method balances by local currency first, then name', () => {
    const sorted = sortPhysicalMethodBalances(
      [
        { paymentMethodId: 1, name: 'Zelle USD', currencyCode: 'USD' },
        { paymentMethodId: 2, name: 'Efectivo Bs.S', currencyCode: 'VES' },
        { paymentMethodId: 3, name: 'Caja Bs.S', currencyCode: 'VES' },
        { paymentMethodId: 4, name: 'Caja COP', currencyCode: 'COP' },
      ],
      DUAL_USD_VES
    );

    assert.deepStrictEqual(sorted.map((row) => row.paymentMethodId), [3, 2, 1, 4]);
  });
});
