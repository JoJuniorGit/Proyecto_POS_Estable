// T5 (Paso 13 / 8.188, S1/S2/S5): lógica pura de los formularios de caja.
// Cubre la equivalencia dual informativa (servidor fija el snapshot), las validaciones con los
// mensajes exactos del contrato, el gating de roles (solo presentación) y el mapeo del cierre
// con conteo USD opcional.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  canUsePettyCash,
  canCloseCashDrawerSession,
  requiresPettyCashCancellationAuthorization,
  computePettyCashEquivalent,
  validatePettyCashDraft,
  validatePettyCashCancelReason,
  validateCashDrawerCloseDraft,
  buildCashDrawerClosePayload,
  validateCashDrawerOpenDraft,
  buildCashDrawerOpenPayload,
  getPettyCashVoucherCode,
  getPettyCashVoucherFileName,
  getPettyCashAuthorizerDisplay,
  summarizePettyCashExpenses,
} from './registerFormLogic.js';
import { PettyCashCurrency, PettyCashStatus } from '../../services/pettyCashApi';

describe('registerFormLogic — roles de la vista de caja', () => {
  it('1. caja chica es Admin/Manager/Cashier; el cierre de sesión es Admin/Manager', () => {
    assert.strictEqual(canUsePettyCash('Admin'), true);
    assert.strictEqual(canUsePettyCash('Manager'), true);
    assert.strictEqual(canUsePettyCash('Cashier'), true);
    assert.strictEqual(canUsePettyCash('Driver'), false);
    assert.strictEqual(canUsePettyCash(null), false);

    assert.strictEqual(canCloseCashDrawerSession('Admin'), true);
    assert.strictEqual(canCloseCashDrawerSession('Manager'), true);
    assert.strictEqual(canCloseCashDrawerSession('Cashier'), false);
    assert.strictEqual(canCloseCashDrawerSession('Driver'), false);
  });

  it('2. la anulación solo es directa para Admin (Manager/Cashier exigen autorización)', () => {
    assert.strictEqual(requiresPettyCashCancellationAuthorization('Admin'), false);
    assert.strictEqual(requiresPettyCashCancellationAuthorization('Manager'), true);
    assert.strictEqual(requiresPettyCashCancellationAuthorization('Cashier'), true);
  });
});

describe('registerFormLogic — equivalencia dual en vivo', () => {
  it('3. Bs.S nominal se proyecta a USD 4dp y el nominal queda redondeado a 2dp', () => {
    const result = computePettyCashEquivalent({
      currency: PettyCashCurrency.BsS,
      amount: 1825,
      exchangeRate: 36.5,
    });

    assert.strictEqual(result.hasRate, true);
    assert.strictEqual(result.bsS, 1825);
    assert.strictEqual(result.usd, 50);
  });

  it('4. USD nominal se proyecta a Bs.S 2dp (AwayFromZero) y conserva su nominal 4dp', () => {
    const result = computePettyCashEquivalent({
      currency: PettyCashCurrency.Usd,
      amount: 12.3456,
      exchangeRate: 36.5,
    });

    assert.strictEqual(result.bsS, 450.61);
    assert.strictEqual(result.usd, 12.3456);
  });

  it('5. sin tasa vigente la equivalencia no fabrica montos', () => {
    const result = computePettyCashEquivalent({
      currency: PettyCashCurrency.BsS,
      amount: 1825,
      exchangeRate: 0,
    });

    assert.strictEqual(result.hasRate, false);
  });
});

describe('registerFormLogic — validaciones del registro (S1)', () => {
  const validDraft = {
    currency: PettyCashCurrency.BsS,
    amount: 100,
    category: 2,
    concept: 'Compra de insumos',
    beneficiary: 'Proveedor X',
    referenceNumber: 'FAC-1',
    exchangeRate: 36.5,
  };

  it('6. acepta un borrador completo', () => {
    assert.strictEqual(validatePettyCashDraft(validDraft), null);
  });

  it('7. exige tasa, monto, categoría, concepto, beneficiario y referencia', () => {
    assert.match(validatePettyCashDraft({ ...validDraft, exchangeRate: 0 }), /tasa de cambio vigente/);
    assert.match(validatePettyCashDraft({ ...validDraft, amount: 0 }), /monto mayor a cero/);
    assert.match(validatePettyCashDraft({ ...validDraft, category: null }), /categoría operativa/);
    assert.match(validatePettyCashDraft({ ...validDraft, concept: '   ' }), /concepto del gasto/);
    assert.match(validatePettyCashDraft({ ...validDraft, beneficiary: '' }), /beneficiario o proveedor/);
    assert.match(validatePettyCashDraft({ ...validDraft, referenceNumber: '' }), /referencia del comprobante/);
  });

  it('8. aplica los topes del contrato (500/150/100)', () => {
    assert.match(
      validatePettyCashDraft({ ...validDraft, concept: 'x'.repeat(501) }),
      /no puede exceder 500/
    );
    assert.match(
      validatePettyCashDraft({ ...validDraft, beneficiary: 'x'.repeat(151) }),
      /no puede exceder 150/
    );
    assert.match(
      validatePettyCashDraft({ ...validDraft, referenceNumber: 'x'.repeat(101) }),
      /no puede exceder 100/
    );
  });
});

describe('registerFormLogic — anulación y cierre', () => {
  it('9. el motivo de anulación es obligatorio con tope de 500', () => {
    assert.match(validatePettyCashCancelReason(''), /motivo de la anulación/);
    assert.match(validatePettyCashCancelReason('   '), /motivo de la anulación/);
    assert.match(validatePettyCashCancelReason('x'.repeat(501)), /no puede exceder 500/);
    assert.strictEqual(validatePettyCashCancelReason('Duplicado'), null);
  });

  it('10. el conteo Bs.S es obligatorio >= 0; el USD vacío es válido', () => {
    assert.match(
      validateCashDrawerCloseDraft({ actualClosingBalanceLocal: '' }),
      /conteo válido de efectivo Bs\.S/
    );
    assert.match(
      validateCashDrawerCloseDraft({ actualClosingBalanceLocal: -1 }),
      /conteo válido de efectivo Bs\.S/
    );
    assert.match(
      validateCashDrawerCloseDraft({ actualClosingBalanceLocal: 100, actualClosingBalanceUsd: -5 }),
      /conteo válido de efectivo USD/
    );
    assert.strictEqual(
      validateCashDrawerCloseDraft({ actualClosingBalanceLocal: 0, actualClosingBalanceUsd: '' }),
      null
    );
    assert.strictEqual(
      validateCashDrawerCloseDraft({ actualClosingBalanceLocal: 100, actualClosingBalanceUsd: 0 }),
      null
    );
  });

  it('11. el payload de cierre mapea el USD opcional (null cuando se omite)', () => {
    assert.deepStrictEqual(
      buildCashDrawerClosePayload({
        actualClosingBalanceLocal: 1250.5,
        actualClosingBalanceUsd: '',
        currentExchangeRate: 36.5,
      }),
      { actualClosingBalanceLocal: 1250.5, currentExchangeRate: 36.5, actualClosingBalanceUsd: null }
    );

    assert.deepStrictEqual(
      buildCashDrawerClosePayload({
        actualClosingBalanceLocal: 1250.5,
        actualClosingBalanceUsd: 20,
        currentExchangeRate: 36.5,
      }),
      { actualClosingBalanceLocal: 1250.5, currentExchangeRate: 36.5, actualClosingBalanceUsd: 20 }
    );
  });
});

describe('registerFormLogic — presentación del listado', () => {
  it('12. deriva el correlativo, el archivo del vale y el autorizador', () => {
    assert.strictEqual(getPettyCashVoucherCode({ voucherNumber: 1 }), 'VCC-000001');
    assert.strictEqual(getPettyCashVoucherCode({ voucherCode: 'VCC-000012' }), 'VCC-000012');
    assert.strictEqual(getPettyCashVoucherFileName({ voucherNumber: 12 }), 'VCC-000012.pdf');
    assert.strictEqual(getPettyCashVoucherFileName({}), 'VCC.pdf');

    assert.strictEqual(getPettyCashAuthorizerDisplay({ authorizedByUserId: 7 }), 'Usuario #7');
    assert.strictEqual(getPettyCashAuthorizerDisplay({ authorizedByUserId: null }), '—');
  });

  it('13. resume solo los gastos activos del turno', () => {
    const summary = summarizePettyCashExpenses([
      { status: PettyCashStatus.Active, amountBsS: 1825, amountUsd: 50 },
      { status: PettyCashStatus.Active, amountBsS: 365, amountUsd: 10 },
      { status: PettyCashStatus.Cancelled, amountBsS: 9999, amountUsd: 9999 },
    ]);

    assert.strictEqual(summary.activeCount, 2);
    assert.strictEqual(summary.totalBsS, 2190);
    assert.strictEqual(summary.totalUsd, 60);
  });
});

describe('registerFormLogic - apertura dual de caja (8.201)', () => {
  it('14. valida ambos fondos y exige tasa configurada', () => {
    assert.strictEqual(validateCashDrawerOpenDraft({
      openingBalanceLocal: 0,
      openingBalanceUsd: 0,
      currentExchangeRate: 50,
    }), null);

    assert.match(validateCashDrawerOpenDraft({
      openingBalanceLocal: -1,
      openingBalanceUsd: 0,
      currentExchangeRate: 50,
    }), /Bs\.S/);

    assert.match(validateCashDrawerOpenDraft({
      openingBalanceLocal: 0,
      openingBalanceUsd: -1,
      currentExchangeRate: 50,
    }), /USD/);

    assert.match(validateCashDrawerOpenDraft({
      openingBalanceLocal: 0,
      openingBalanceUsd: 0,
      currentExchangeRate: 0,
    }), /tasa/);
  });

  it('15. el payload envía ambos fondos y la tasa, con 0 como valor válido', () => {
    assert.deepStrictEqual(buildCashDrawerOpenPayload({
      openingBalanceLocal: 1250.5,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
    }), {
      openingBalanceLocal: 1250.5,
      openingBalanceUsd: 20,
      currentExchangeRate: 36.5,
    });

    assert.deepStrictEqual(buildCashDrawerOpenPayload({}), {
      openingBalanceLocal: 0,
      openingBalanceUsd: 0,
      currentExchangeRate: 0,
    });
  });
});
