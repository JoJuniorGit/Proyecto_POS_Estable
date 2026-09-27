import { describe, it } from 'node:test';
import assert from 'node:assert';
import { buildCheckoutPreviewRequest, computeCheckoutGate } from './CheckoutModal.jsx';

const baseGateInput = {
  preview: { isFullyPaid: true, roundingAdjustment: 0.35 },
  previewSignature: 'sig-a',
  currentSignature: 'sig-a',
  previewFailed: false,
  hasValidPayments: true,
  isOverrideSale: false,
  isDefaultCust: false,
  isPendingPickup: false,
};

describe('CheckoutModal preview gate [8.5-WEB1]', () => {
  it('1. No preview -> finalize blocked and no fiscal adjustment fallback', () => {
    const gate = computeCheckoutGate({ ...baseGateInput, preview: null, previewSignature: null });

    assert.strictEqual(gate.isPreviewFresh, false);
    assert.strictEqual(gate.isFullLiquidation, false);
    assert.strictEqual(gate.canFinalize, false);
    assert.strictEqual(gate.roundingAdjustment, null);
  });

  it('2. Stale preview signature (payments mutated after request) -> blocked', () => {
    const gate = computeCheckoutGate({ ...baseGateInput, previewSignature: 'sig-old', currentSignature: 'sig-new' });

    assert.strictEqual(gate.isPreviewFresh, false);
    assert.strictEqual(gate.isFullLiquidation, false);
    assert.strictEqual(gate.canFinalize, false);
    assert.strictEqual(gate.roundingAdjustment, null);
  });

  it('3. Preview failed -> blocked even with matching signature', () => {
    const gate = computeCheckoutGate({ ...baseGateInput, previewFailed: true });

    assert.strictEqual(gate.canFinalize, false);
    assert.strictEqual(gate.roundingAdjustment, null);
  });

  it('4. Matching preview + full liquidation -> enabled with authoritative rounding adjustment', () => {
    const gate = computeCheckoutGate(baseGateInput);

    assert.strictEqual(gate.isPreviewFresh, true);
    assert.strictEqual(gate.isFullLiquidation, true);
    assert.strictEqual(gate.canFinalize, true);
    assert.strictEqual(gate.roundingAdjustment, 0.35);
  });

  it('5. Matching preview + not fully paid on a normal sale -> blocked', () => {
    const gate = computeCheckoutGate({ ...baseGateInput, preview: { isFullyPaid: false, roundingAdjustment: 0 } });

    assert.strictEqual(gate.isPreviewFresh, true);
    assert.strictEqual(gate.canFinalize, false);
  });

  it('6. Matching preview + partial advance on an open account -> enabled', () => {
    const gate = computeCheckoutGate({
      ...baseGateInput,
      isOverrideSale: true,
      preview: { isFullyPaid: false, roundingAdjustment: 0 },
    });

    assert.strictEqual(gate.canFinalize, true);
  });

  it('7. Custody pickup requires full liquidation and a real customer', () => {
    const withoutCustomer = computeCheckoutGate({ ...baseGateInput, isPendingPickup: true, isDefaultCust: true });
    assert.strictEqual(withoutCustomer.canFinalize, false);
    assert.strictEqual(withoutCustomer.isCustodyAllowed, false);

    const allowed = computeCheckoutGate({ ...baseGateInput, isPendingPickup: true, isDefaultCust: false });
    assert.strictEqual(allowed.isCustodyAllowed, true);
    assert.strictEqual(allowed.canFinalize, true);
  });

  it('8. buildCheckoutPreviewRequest merges previous payments (open account) with current payments', () => {
    const request = buildCheckoutPreviewRequest({
      saleId: 42,
      exchangeRate: 50,
      payments: [{ methodId: 1, amountUsd: 10, amountBsS: 500, reference: 'REF-1' }],
      overrideSale: { payments: [{ paymentMethodId: 2, amount: 3, amountBsS: 150 }] },
    });

    assert.strictEqual(request.saleId, 42);
    assert.strictEqual(request.exchangeRate, 50);
    assert.deepStrictEqual(request.payments, [
      { paymentMethodId: 2, amount: 3, amountBsS: 150 },
      { paymentMethodId: 1, amount: 10, amountBsS: 500, amountLocal: 500, referenceNumber: 'REF-1' },
    ]);
  });

  it('9. The preview signature changes whenever payments change (invalidation)', () => {
    const build = (amountUsd) => JSON.stringify(buildCheckoutPreviewRequest({
      saleId: 1,
      exchangeRate: 50,
      payments: [{ methodId: 1, amountUsd, amountBsS: amountUsd * 50 }],
      overrideSale: null,
    }));

    assert.notStrictEqual(build(10), build(20));
    assert.strictEqual(build(10), build(10));
  });
});
