import { describe, test } from 'node:test';
import assert from 'node:assert';
import {
  buildDeliveryPayload,
  clampQuantity,
  computeProgress,
  isConfirmEnabled,
} from './deliveryProgress.js';

describe('delivery progress helpers', () => {
  test('clampQuantity accepts fractions and marks values outside the pending range', () => {
    assert.deepEqual(clampQuantity('1.25', 2), { value: 1.25, isInvalid: false });
    assert.deepEqual(clampQuantity('4', 3), { value: 3, isInvalid: true });
    assert.deepEqual(clampQuantity('-0.5', 3), { value: 0, isInvalid: true });
    assert.deepEqual(clampQuantity('', 3), { value: 0, isInvalid: false });
    assert.deepEqual(clampQuantity('invalid', 3), { value: 0, isInvalid: true });
  });

  test('computeProgress aggregates delivered and pending fractional units', () => {
    assert.deepEqual(
      computeProgress([
        { totalQuantity: 6, deliveredQuantity: 2, pendingQuantity: 4 },
        { totalQuantity: 4, deliveredQuantity: 2, pendingQuantity: 2 },
      ]),
      {
        totalUnits: 10,
        deliveredUnits: 4,
        pendingUnits: 6,
        percent: 40,
        label: 'Retirado: 4/10',
      }
    );

    assert.deepEqual(computeProgress([
      { totalQuantity: 2.5, deliveredQuantity: 0.75, pendingQuantity: 1.75 },
    ]), {
      totalUnits: 2.5,
      deliveredUnits: 0.75,
      pendingUnits: 1.75,
      percent: 30,
      label: 'Retirado: 0.75/2.5',
    });
  });

  test('isConfirmEnabled requires a positive valid quantity', () => {
    assert.equal(isConfirmEnabled([{ quantity: 0, pendingQuantity: 3 }]), false);
    assert.equal(isConfirmEnabled([{ quantity: 1.5, pendingQuantity: 3 }]), true);
    assert.equal(isConfirmEnabled([{ quantity: 1, pendingQuantity: 3, isInvalid: true }]), false);
    assert.equal(isConfirmEnabled([{ quantity: 4, pendingQuantity: 3 }]), false);
  });

  test('buildDeliveryPayload includes only positive valid rows', () => {
    assert.deepEqual(buildDeliveryPayload([
      { saleItemId: 11, quantity: 0 },
      { saleItemId: 12, quantity: '1.25' },
      { saleItemId: 13, quantity: 2, isInvalid: true },
      { saleItemId: 14, quantity: -1 },
    ]), [
      { saleItemId: 12, quantity: 1.25 },
    ]);
  });
});
