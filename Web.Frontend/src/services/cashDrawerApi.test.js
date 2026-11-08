import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  buildCashAdvancePayload,
  formatPhysicalMethodAmount,
  resolveDispensedMethod,
  validateDispensedBalance,
} from './cashDrawerApi.js';

const METHOD = Object.freeze({
  paymentMethodId: 10,
  name: 'Caja Bs.S',
  currencyCode: 'VES',
  currencySymbol: 'Bs.S',
  currentBalance: 500,
  isActive: true,
  canDispense: true,
});

describe('cashDrawerApi - 8.212-WU5', () => {
  it('keeps the legacy advance payload when no physical dispenser is selected', () => {
    const payload = buildCashAdvancePayload({
      sessionId: 1,
      requestedAmountLocal: 100,
      paymentMethodId: 2,
      paymentMethodName: 'Transferencia',
      isTransfer: true,
      exchangeRate: 50,
    });

    assert.equal(payload.dispensedCashPaymentMethodId, undefined);
    assert.equal(payload.dispensedAmount, undefined);
    assert.equal(payload.receivedPaymentMethodId, undefined);
  });

  it('adds the dispensed and received method fields for a method-aware advance', () => {
    const payload = buildCashAdvancePayload({
      sessionId: 1,
      requestedAmountLocal: 100,
      paymentMethodId: 2,
      paymentMethodName: 'Transferencia',
      isTransfer: true,
      exchangeRate: 50,
      dispensedCashPaymentMethodId: 10,
      dispensedAmount: 100,
      receivedPaymentMethodId: 2,
    });

    assert.equal(payload.dispensedCashPaymentMethodId, 10);
    assert.equal(payload.dispensedAmount, 100);
    assert.equal(payload.receivedPaymentMethodId, 2);
  });

  it('auto-selects the sole method and the first dispensable method', () => {
    assert.equal(resolveDispensedMethod([METHOD], '')?.paymentMethodId, 10);
    const disabled = { ...METHOD, paymentMethodId: 11, canDispense: false };
    assert.equal(resolveDispensedMethod([disabled, METHOD], '')?.paymentMethodId, 10);
    assert.equal(resolveDispensedMethod([METHOD], '10')?.paymentMethodId, 10);
  });

  it('blocks inactive and insufficient methods with the exact message shape', () => {
    assert.match(validateDispensedBalance(null, 1), /Seleccione el método físico/);
    assert.match(
      validateDispensedBalance({ ...METHOD, isActive: false }, 1),
      /no está activo para dispensar/
    );

    const message = validateDispensedBalance(METHOD, 700);
    assert.match(message, /^Existencia insuficiente en Caja Bs\.S \(VES\): disponible Bs\.S 500,00, requerido Bs\.S 700,00\.$/);
  });

  it('formats the method amount with its own symbol', () => {
    assert.match(formatPhysicalMethodAmount(METHOD, 25), /^Bs\.S 25,00$/);
  });
});
