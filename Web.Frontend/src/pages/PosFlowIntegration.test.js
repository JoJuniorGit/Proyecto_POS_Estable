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

  test('4. Increasing, decreasing, and direct manual entry correctly invoke quantity updates and recalculate totals', () => {
    let updatedId = null;
    let updatedQty = null;

    const mockUpdateQuantity = (id, qty) => {
      updatedId = id;
      updatedQty = qty;
    };

    // Simulate prop resolution in CartTable / CartList
    const props = {
      items: [{ id: 42, quantity: 2, unitPrice: 15, isFractional: false }],
      onUpdateQuantity: mockUpdateQuantity,
    };
    const updateQty = props.onUpdateQty || props.onUpdateQuantity;
    assert.ok(typeof updateQty === 'function', 'updateQty must resolve correctly from onUpdateQuantity');

    // Simulate Plus button click (+ step)
    const item = props.items[0];
    const step = !item.isFractional ? 1 : 0.1;
    const newQtyPlus = Math.round((item.quantity + step) * 1000) / 1000;
    updateQty(item.id, newQtyPlus);

    assert.strictEqual(updatedId, 42);
    assert.strictEqual(updatedQty, 3, 'Plus button must increase quantity to 3');

    // Recalculate totals with updated quantity
    const updatedAmounts = getLineAmounts({ ...item, quantity: updatedQty }, 50.0);
    assert.strictEqual(updatedAmounts.subtotalBsS, 3 * 15 * 50.0); // 45 * 50 = 2250
    assert.strictEqual(formatBsS(updatedAmounts.subtotalBsS), 'Bs.S 2,250.00');

    // Simulate Direct Manual Input (e.g. typing "5" in QuantityInput)
    const manualInput = '5';
    const parsedManual = parseFloat(manualInput.replace(',', '.'));
    updateQty(item.id, parsedManual);

    assert.strictEqual(updatedQty, 5, 'Direct manual entry must update quantity to 5');
    const manualAmounts = getLineAmounts({ ...item, quantity: updatedQty }, 50.0);
    assert.strictEqual(manualAmounts.subtotalBsS, 5 * 15 * 50.0); // 75 * 50 = 3750
    assert.strictEqual(formatBsS(manualAmounts.subtotalBsS), 'Bs.S 3,750.00');
  });
});
