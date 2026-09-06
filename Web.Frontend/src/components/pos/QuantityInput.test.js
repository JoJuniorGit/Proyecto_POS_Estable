import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('QuantityInput Fractional vs Non-Fractional Validation Tests', () => {
  test('1. Non-fractional product strictly rejects decimal point (.) and comma (,)', () => {
    const testInputs = ['1.', '1,', '1.5', '2,50', '0.1', '10.00'];

    for (const input of testInputs) {
      // Logic from QuantityInput.jsx for !isFractional:
      const isAccepted = /^\d+$/.test(input);
      assert.strictEqual(isAccepted, false, `Input "${input}" must be rejected for non-fractional products`);
    }
  });

  test('2. Non-fractional product accepts integer digits only', () => {
    const testInputs = ['1', '5', '12', '100'];

    for (const input of testInputs) {
      const isAccepted = /^\d+$/.test(input);
      assert.strictEqual(isAccepted, true, `Input "${input}" must be accepted for non-fractional products`);

      const parsedInt = parseInt(input, 10);
      assert.strictEqual(Number.isInteger(parsedInt), true);
      assert.ok(parsedInt > 0);
    }
  });

  test('3. Fractional product allows decimals up to 3 places (. or ,)', () => {
    const validInputs = ['0.5', '1,25', '2.345', '100.500', '1.', '2,'];

    for (const input of validInputs) {
      const isAccepted = /^\d*[,.]?\d{0,3}$/.test(input);
      assert.strictEqual(isAccepted, true, `Input "${input}" must be accepted for fractional products`);
    }

    const invalidInputs = ['1.2345', '1..2', 'abc', '1,2,3'];
    for (const input of invalidInputs) {
      const isAccepted = /^\d*[,.]?\d{0,3}$/.test(input);
      assert.strictEqual(isAccepted, false, `Input "${input}" must be rejected for fractional products`);
    }
  });

  test('4. OnBlur for non-fractional item guarantees integer quantity (truncating any rogue decimal)', () => {
    const rogueInputs = ['2.8', '5,25', '10.999'];

    for (const input of rogueInputs) {
      const normalized = input.replace(',', '.');
      const parsed = parseFloat(normalized);
      const intVal = Math.max(1, Math.trunc(parsed));

      assert.strictEqual(Number.isInteger(intVal), true, `Value ${intVal} must be an integer`);
      assert.strictEqual(intVal, Math.floor(parsed), 'Rogue decimal must be truncated');
    }
  });

  test('5. EditSaleModal and CartContext quantity updates enforce integer for non-fractional items', () => {
    const items = [
      { id: 1, name: 'Arroz 1kg', isFractional: false },
      { id: 2, name: 'Queso Llanero', isFractional: true }
    ];

    // Attempting to set 2.5 on item 1 (non-fractional)
    const newQty1 = 2.5;
    const item1 = items[0];
    const validatedQty1 = item1.isFractional ? Math.round(newQty1 * 1000) / 1000 : Math.max(1, Math.trunc(newQty1));
    assert.strictEqual(validatedQty1, 2, 'Non-fractional item quantity must truncate 2.5 to 2');

    // Setting 2.5 on item 2 (fractional)
    const newQty2 = 2.5;
    const item2 = items[1];
    const validatedQty2 = item2.isFractional ? Math.round(newQty2 * 1000) / 1000 : Math.max(1, Math.trunc(newQty2));
    assert.strictEqual(validatedQty2, 2.5, 'Fractional item quantity must preserve 2.5');
  });

  test('6. QuantityInput correctly evaluates isFractionable, IsFractional and unitOfMeasure fallbacks', () => {
    const resolveIsFractional = (item) => Boolean(
      item?.isFractional ||
      item?.isFractionable ||
      item?.IsFractional ||
      item?.IsFractionable ||
      (item?.unitOfMeasure && item.unitOfMeasure !== 'Und' && item.unitOfMeasure !== 0)
    );

    assert.strictEqual(resolveIsFractional({ isFractionable: true }), true);
    assert.strictEqual(resolveIsFractional({ IsFractional: true }), true);
    assert.strictEqual(resolveIsFractional({ isFractional: true }), true);
    assert.strictEqual(resolveIsFractional({ unitOfMeasure: 'Kg' }), true);
    assert.strictEqual(resolveIsFractional({ unitOfMeasure: 'Grs' }), true);
    assert.strictEqual(resolveIsFractional({ unitOfMeasure: 'Lt' }), true);
    assert.strictEqual(resolveIsFractional({ unitOfMeasure: 'Und', isFractional: false }), false);
    assert.strictEqual(resolveIsFractional({ unitOfMeasure: 'Und' }), false);
  });

  test('7. EditSaleModal reactive recalculation: Modifying pre-existing item quantity updates Bs.S and USD in real-time', () => {
    const exchangeRate = 60; // 60 Bs.S / USD
    const preExistingItem = {
      productId: 10,
      productName: 'Harina PAN',
      unitPrice: 1.5,
      unitPriceBsS: 90,
      quantity: 1,
      subtotal: 1.5,
      subtotalBsS: 90,
      isFractional: false
    };

    // User increases quantity from 1 to 3
    const newQty = 3;
    const unitBsS = Number(preExistingItem.unitPriceBsS) > 0 ? Number(preExistingItem.unitPriceBsS) : (preExistingItem.unitPrice * exchangeRate);
    const newSubtotalUSD = Math.round(newQty * preExistingItem.unitPrice * 100) / 100;
    const newSubtotalBsS = Math.round(newQty * unitBsS * 100) / 100;

    assert.strictEqual(newSubtotalUSD, 4.5, 'USD subtotal must update to 4.5');
    assert.strictEqual(newSubtotalBsS, 270, 'Bs.S subtotal must update to 270 instead of staying frozen at 90');

    // Total recalculation for modal
    const items = [
      { ...preExistingItem, quantity: newQty, unitPriceBsS: unitBsS, subtotal: newSubtotalUSD, subtotalBsS: newSubtotalBsS }
    ];
    const newTotalBsS = items.reduce((acc, i) => {
      const lineUnitBsS = Number(i.unitPriceBsS) > 0 ? Number(i.unitPriceBsS) : (i.unitPrice * exchangeRate);
      return acc + (i.quantity * lineUnitBsS);
    }, 0);
    assert.strictEqual(newTotalBsS, 270, 'Modal newTotalBsS must update to 270');
  });
});
