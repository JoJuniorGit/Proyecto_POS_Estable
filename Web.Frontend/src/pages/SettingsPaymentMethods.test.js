// P0 (S1/S2): the payment-method create form sends the optional commission block, never a
// customer-facing surcharge. The backend creates the method and its fee rule atomically.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { buildPaymentMethodCommission } from './SettingsPaymentMethods.jsx';

describe('SettingsPaymentMethods - commission creation payload', () => {
  it('builds a percentage commission for a digital method', () => {
    const result = buildPaymentMethodCommission({
      isCash: false,
      feeType: 0,
      percentage: '0,75',
      fixedAmount: '',
      currencyCode: 'USD',
    });

    assert.strictEqual(result.hasPositiveCommission, true);
    assert.deepStrictEqual(result.commission, {
      feeType: 0,
      percentage: 0.75,
      fixedAmount: null,
      currencyCode: 'USD',
      isActive: true,
    });
  });

  it('builds a fixed per-transaction commission', () => {
    const result = buildPaymentMethodCommission({
      isCash: false,
      feeType: 1,
      percentage: '',
      fixedAmount: '1.60',
      currencyCode: 'USD',
    });

    assert.strictEqual(result.hasPositiveCommission, true);
    assert.strictEqual(result.commission.feeType, 1);
    assert.strictEqual(result.commission.percentage, null);
    assert.strictEqual(result.commission.fixedAmount, 1.6);
  });

  it('builds a combined percentage plus fixed commission', () => {
    const result = buildPaymentMethodCommission({
      isCash: false,
      feeType: 2,
      percentage: '2.50',
      fixedAmount: '0',
      currencyCode: 'USD',
    });

    assert.strictEqual(result.hasPositiveCommission, true);
    assert.strictEqual(result.commission.feeType, 2);
    assert.strictEqual(result.commission.percentage, 2.5);
    assert.strictEqual(result.commission.fixedAmount, 0);
  });

  it('omits the commission for 0/0 and cash methods', () => {
    const zero = buildPaymentMethodCommission({
      isCash: false,
      feeType: 0,
      percentage: '0',
      fixedAmount: '',
      currencyCode: 'USD',
    });
    assert.strictEqual(zero.hasPositiveCommission, false);
    assert.strictEqual(zero.commission, null);

    const cash = buildPaymentMethodCommission({
      isCash: true,
      feeType: 0,
      percentage: '2.5',
      fixedAmount: '',
      currencyCode: 'USD',
    });
    assert.strictEqual(cash.hasPositiveAmount, true);
    assert.strictEqual(cash.hasPositiveCommission, false);
    assert.strictEqual(cash.commission, null);
  });

  it('trims the commission currency', () => {
    const result = buildPaymentMethodCommission({
      isCash: false,
      feeType: 0,
      percentage: '0.75',
      fixedAmount: '',
      currencyCode: ' USD ',
    });

    assert.strictEqual(result.commission.currencyCode, 'USD');
  });
});
