// 8.187-T5 (Paso 11/12, F4, S2/S4/S6): integración del modo Crédito en el CheckoutModal web.
// Prueba el selector puro que mapea el estado del modal (preview canónico, perfil crediticio y
// totales exhibidos) a la vista de crédito: financiado, vencimiento, bloqueos y sobregiro.
// El modo Contado queda inerte (sin financiamiento ni gates de crédito).
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { resolveCheckoutCreditView } from './CheckoutModal.jsx';
import { CREDIT_STATUS } from '../credit/creditLogic.js';

const healthyProfile = {
  customerId: 7,
  isActive: true,
  isCreditEnabled: true,
  creditStatus: CREDIT_STATUS.ACTIVE,
  overdueCount: 0,
  creditLimitUSD: 1000,
  currentDebtUSD: 300,
  availableCreditUSD: 700,
  paymentTermDays: 30,
};

const base = {
  isCreditMode: true,
  hasRegisteredCustomer: true,
  isCreditProfileLoading: false,
  creditProfileError: null,
  creditProfile: healthyProfile,
  hasFreshPreview: true,
  preview: { remainingBalanceUSD: 101.37, remainingBalanceBsS: 3700.01 },
  fallbackFinancedUSD: 250,
  fallbackFinancedBsS: 9125,
};

describe('CheckoutModal — modo Crédito (total, mixto y sobregiro)', () => {
  it('1. keeps the cash mode inert: no financing, no credit gate', () => {
    const view = resolveCheckoutCreditView({ ...base, isCreditMode: false });

    assert.strictEqual(view.financedUSD, 0);
    assert.strictEqual(view.financedBsS, 0);
    assert.strictEqual(view.canFinalize, false);
    assert.strictEqual(view.isOverLimit, false);
    assert.strictEqual(view.profileBlockMessage, null);
  });

  it('2. crédito total: without payments the whole sale is financed (fallback of the displayed total)', () => {
    const view = resolveCheckoutCreditView({
      ...base,
      hasFreshPreview: false,
      preview: null,
    });

    assert.strictEqual(view.financedUSD, 250);
    assert.strictEqual(view.financedBsS, 9125);
    assert.strictEqual(view.canFinalize, true);
    assert.ok(view.dueDate instanceof Date);
  });

  it('3. crédito mixto: the fresh server preview fixes the financed remainder of the initial payment', () => {
    const view = resolveCheckoutCreditView(base);

    assert.strictEqual(view.financedUSD, 101.37);
    assert.strictEqual(view.financedBsS, 3700.01);
    assert.strictEqual(view.canFinalize, true);
  });

  it('4. sobregiro: blocks finalization with the WPF authorization message', () => {
    const view = resolveCheckoutCreditView({
      ...base,
      preview: { remainingBalanceUSD: 700.01, remainingBalanceBsS: 25550 },
    });

    assert.strictEqual(view.isOverLimit, true);
    assert.match(view.overLimitMessage, /caja WPF/);
    assert.strictEqual(view.canFinalize, false);
  });

  it('5. blocks credit without a registered customer and while the profile is unavailable', () => {
    assert.strictEqual(resolveCheckoutCreditView({ ...base, hasRegisteredCustomer: false }).canFinalize, false);
    assert.strictEqual(resolveCheckoutCreditView({ ...base, isCreditProfileLoading: true }).canFinalize, false);
    assert.strictEqual(resolveCheckoutCreditView({ ...base, creditProfileError: 'sin red' }).canFinalize, false);
    assert.strictEqual(resolveCheckoutCreditView({ ...base, creditProfile: null }).canFinalize, false);
    assert.strictEqual(
      resolveCheckoutCreditView({ ...base, creditProfile: { ...healthyProfile, creditStatus: CREDIT_STATUS.MANUALLY_BLOCKED } }).canFinalize,
      false
    );
  });

  it('6. exposes the due date only when the credit profile is known', () => {
    const withProfile = resolveCheckoutCreditView(base);
    const withoutProfile = resolveCheckoutCreditView({ ...base, creditProfile: null });

    assert.ok(withProfile.dueDate instanceof Date);
    assert.strictEqual(withoutProfile.dueDate, null);
  });
});
