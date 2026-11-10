import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  buildCashDrawerSessionSummary,
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

describe('cash drawer session summary by currency', () => {
  it('builds one column per currency with opening, income, expense and expected', () => {
    const summary = buildCashDrawerSessionSummary(
      DUAL_USD_VES,
      { openingBalanceLocal: 941.235, openingBalanceUsd: 0 },
      [
        {
          type: 0,
          source: 1,
          isPhysicalCash: true,
          currencyCode: 'VES',
          amountLocal: 49.214,
          amountUsd: 1.10352,
          exchangeRate: 44.59,
        },
        {
          type: 0,
          source: 1,
          isPhysicalCash: true,
          currencyCode: 'USD',
          amountLocal: 500,
          amountUsd: 10,
          exchangeRate: 50,
        },
        {
          type: 1,
          source: 6,
          isPhysicalCash: true,
          currencyCode: 'VES',
          amountLocal: 5,
          amountUsd: 0.1,
          exchangeRate: 50,
        },
        {
          type: 0,
          source: 0,
          isPhysicalCash: true,
          currencyCode: 'VES',
          amountLocal: 941.235,
          amountUsd: 18.8247,
          exchangeRate: 50,
        },
      ]
    );

    assert.deepStrictEqual(summary.map((row) => row.code), ['VES', 'USD']);
    assert.strictEqual(summary[0].isLocal, true);
    assert.strictEqual(summary[0].opening, 941.235);
    assert.strictEqual(summary[0].income, 49.214);
    assert.strictEqual(summary[0].expense, 5);
    assert.strictEqual(Number(summary[0].expected.toFixed(3)), 985.449);
    assert.strictEqual(summary[1].opening, 0);
    assert.strictEqual(summary[1].income, 10);
    assert.strictEqual(summary[1].expense, 0);
    assert.strictEqual(summary[1].expected, 10);
  });

  it('uses the single profile currency and its opening balance', () => {
    const summary = buildCashDrawerSessionSummary(
      SINGLE_EUR,
      { openingBalanceLocal: 100, openingBalanceUsd: 0 },
      [
        { type: 0, source: 1, isPhysicalCash: true, currencyCode: 'EUR', amountLocal: 20, amountUsd: 22, exchangeRate: 0.91 },
        { type: 1, source: 6, isPhysicalCash: true, currencyCode: 'EUR', amountLocal: 5, amountUsd: 5.5, exchangeRate: 0.91 },
      ]
    );

    assert.deepStrictEqual(summary.map((row) => row.code), ['EUR']);
    assert.strictEqual(summary[0].isLocal, true);
    assert.strictEqual(summary[0].opening, 100);
    assert.strictEqual(summary[0].income, 20);
    assert.strictEqual(summary[0].expense, 5);
    assert.strictEqual(summary[0].expected, 115);
  });

  it('falls back to the local currency when a legacy transaction has no currency code', () => {
    const summary = buildCashDrawerSessionSummary(
      DUAL_USD_VES,
      { openingBalanceLocal: 0, openingBalanceUsd: 0 },
      [{ type: 0, source: 1, isPhysicalCash: true, amountLocal: 10, amountUsd: 0.2, exchangeRate: 50 }]
    );

    assert.strictEqual(summary[0].income, 10);
    assert.strictEqual(summary[1].income, 0);
  });

  it('derives the primary amount from the rate when amountUsd is absent', () => {
    const summary = buildCashDrawerSessionSummary(
      DUAL_USD_VES,
      { openingBalanceLocal: 0, openingBalanceUsd: 0 },
      [{ type: 0, source: 1, isPhysicalCash: true, currencyCode: 'USD', amountLocal: 500, amountUsd: 0, exchangeRate: 50 }]
    );

    assert.strictEqual(summary[1].income, 10);
    assert.strictEqual(summary[1].expected, 10);
  });

  it('nets petty cash reversals and excludes closing from the fallback summary', () => {
    const summary = buildCashDrawerSessionSummary(
      DUAL_USD_VES,
      { openingBalanceLocal: 100, openingBalanceUsd: 0 },
      [
        { type: 0, source: 0, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 100, amountUsd: 2 },
        { type: 0, source: 1, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 50, amountUsd: 1 },
        { type: 1, source: 6, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 10, amountUsd: 0.2 },
        { type: 1, source: 9, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 2, amountUsd: 0.04 },
        { type: 0, source: 10, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 1, amountUsd: 0.02 },
        { type: 1, source: 4, isPhysicalCash: true, currencyCode: 'VES', amountLocal: 100, amountUsd: 2 },
      ]
    );

    assert.strictEqual(summary[0].opening, 100);
    assert.strictEqual(summary[0].income, 50);
    assert.strictEqual(summary[0].expense, 11);
    assert.strictEqual(summary[0].expected, 139);
  });
});
