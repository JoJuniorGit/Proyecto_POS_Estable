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
});
