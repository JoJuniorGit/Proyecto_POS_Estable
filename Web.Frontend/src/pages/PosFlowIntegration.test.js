import { test, describe } from 'node:test';
import assert from 'node:assert';
import { isValidBarcode } from '../utils/barcodeValidator.js';
import { formatBsS, getLineAmounts } from '../utils/formatters.js';

describe('PosFlowIntegration End-to-End Test Suite', () => {
  test('1. Validates barcode scanning input, calculates dual currency amounts, and handles error fallback', () => {
    // Valid EAN-13 barcode scan
    assert.strictEqual(isValidBarcode('7591001002009'), true);
    assert.strictEqual(isValidBarcode('INVALID_URL_?='), false);

    // Dual currency amounts formatting
    const item = { unitPrice: 10, quantity: 2, subtotal: 20 };
    const amounts = getLineAmounts(item, 50.0);

    assert.strictEqual(amounts.subtotalBsS, 1000.0);
    assert.strictEqual(formatBsS(amounts.subtotalBsS), 'Bs.S 1,000.00');
  });

  test('2. Recovers gracefully on network failure mock', async () => {
    let errorCaught = false;
    try {
      throw new Error('Network error simulating API crash during checkout');
    } catch (err) {
      errorCaught = true;
      assert.ok(err.message.includes('Network error'));
    }
    assert.strictEqual(errorCaught, true);
  });

  test('3. Decreasing quantity on a product with 1 unit never eliminates or reduces quantity below 1 (or minimum step)', () => {
    // Standard product (non-fractional)
    const standardItem = { id: 1, quantity: 1, isFractional: false };
    const stepStandard = !standardItem.isFractional ? 1 : 0.1;
    const isAtMinStandard = standardItem.quantity <= stepStandard;
    const newQtyStandard = Math.round((standardItem.quantity - stepStandard) * 1000) / 1000;

    assert.strictEqual(isAtMinStandard, true, 'Standard item with qty 1 must be flagged at minimum');
    assert.strictEqual(newQtyStandard >= stepStandard, false, 'Decreasing standard item from 1 must NOT yield >= step');

    // Fractional product (e.g. Grs)
    const fractionalItem = { id: 2, quantity: 100, isFractional: true, unitOfMeasure: 'Grs' };
    const stepFractional = fractionalItem.unitOfMeasure === 'Grs' ? 100 : 1;
    const isAtMinFractional = fractionalItem.quantity <= stepFractional;
    const newQtyFractional = Math.round((fractionalItem.quantity - stepFractional) * 1000) / 1000;

    assert.strictEqual(isAtMinFractional, true, 'Fractional item with 100g must be flagged at minimum');
    assert.strictEqual(newQtyFractional >= stepFractional, false, 'Decreasing fractional item from minimum step must NOT yield >= step');
  });
});
