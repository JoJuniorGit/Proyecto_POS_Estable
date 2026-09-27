import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  CashTransactionSource,
  SOURCE_DEFINITIONS,
  getSourceLabel,
  getSourceIdByFilterKey,
  matchesSourceFilter,
} from './cashTransactionSource.js';

describe('cashTransactionSource — REQ-ADB-05 contract-aligned mapping', () => {
  it('exposes the server contract ordinals', () => {
    assert.equal(CashTransactionSource.Opening, 0);
    assert.equal(CashTransactionSource.SalePayment, 1);
    assert.equal(CashTransactionSource.CashAdvance, 2);
    assert.equal(CashTransactionSource.ManualAdjustment, 3);
    assert.equal(CashTransactionSource.Closing, 4);
    assert.equal(CashTransactionSource.CashIn, 5);
    assert.equal(CashTransactionSource.CashOut, 6);
  });

  it('defines one {id, key, label} table covering every contract value', () => {
    const ids = SOURCE_DEFINITIONS.map((definition) => definition.id);

    assert.deepEqual([...ids].sort((a, b) => a - b), [0, 1, 2, 3, 4, 5, 6]);
    for (const definition of SOURCE_DEFINITIONS) {
      assert.equal(typeof definition.id, 'number');
      assert.ok(definition.key.length > 0);
      assert.ok(definition.label.length > 0);
    }
  });

  it('labels 2 as Adelanto Efectivo, 3 as Ajuste Manual and 4 as Cierre Caja', () => {
    assert.equal(getSourceLabel(2), 'Adelanto Efectivo');
    assert.equal(getSourceLabel(3), 'Ajuste Manual');
    assert.equal(getSourceLabel(4), 'Cierre Caja');
    assert.equal(getSourceLabel(CashTransactionSource.CashAdvance), 'Adelanto Efectivo');
    assert.equal(getSourceLabel('CashAdvance'), 'Adelanto Efectivo');
    assert.equal(getSourceLabel('SalePayment'), 'Venta POS');
    assert.equal(getSourceLabel('Sale'), 'Venta POS');
  });

  it('resolves the advance filter to CashAdvance (2) and never to Closing (4)', () => {
    assert.equal(getSourceIdByFilterKey('advance'), 2);
    assert.equal(matchesSourceFilter('advance', 2), true);
    assert.equal(matchesSourceFilter('advance', 4), false);
    assert.equal(matchesSourceFilter('advance', 3), false);
  });

  it('resolves every filter key from the same table', () => {
    assert.equal(matchesSourceFilter('opening', 0), true);
    assert.equal(matchesSourceFilter('sale', 1), true);
    assert.equal(matchesSourceFilter('cashin', 5), true);
    assert.equal(matchesSourceFilter('cashout', 6), true);
    assert.equal(matchesSourceFilter('all', 4), true);
    assert.equal(matchesSourceFilter('unknown', 2), false);
  });

  it('never labels a non-advance value as an advance', () => {
    for (const source of [0, 1, 3, 4, 5, 6]) {
      assert.notEqual(getSourceLabel(source), 'Adelanto Efectivo');
    }
  });
});
