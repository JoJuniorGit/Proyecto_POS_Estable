// 8.187-T5 (Paso 11/12, F4): lógica pura del crédito CxC en el POS web.
// Cubre perfilamiento (bloqueos/validación), elegibilidad del checkout y validaciones del
// abono (FIFO/explícita) sin tocar red, DOM ni políticas de servidor: solo presentación.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  CREDIT_STATUS,
  creditStatusLabel,
  isCreditProfileBlocked,
  creditProfileBlockMessage,
  validateCreditProfileUpdate,
  computeCreditCheckoutState,
  creditOverLimitMessage,
  creditDueDate,
  formatDateVe,
  formatDueDateDisplay,
  validateCreditPaymentInput,
  receivableBsSProyeccion,
  creditPaymentRemaining,
  hasOverdueAlert,
  isReceivableOpen,
  isReceivableCancelable,
  canReturnReceivable,
  RECEIVABLE_STATUS_OPTIONS,
  parseCreditAmount,
} from './creditLogic.js';

const healthyProfile = {
  customerId: 7,
  isActive: true,
  isCreditEnabled: true,
  creditStatus: 0,
  overdueCount: 0,
  creditLimitUSD: 1000,
  currentDebtUSD: 300,
  availableCreditUSD: 700,
  utilizationPercent: 30,
  overdueUSD: 0,
};

describe('creditLogic — estado y bloqueos del perfil', () => {
  it('1. maps the numeric CreditStatus enum to its Spanish label', () => {
    assert.strictEqual(CREDIT_STATUS.ACTIVE, 0);
    assert.strictEqual(CREDIT_STATUS.SUSPENDED_BY_MORA, 1);
    assert.strictEqual(CREDIT_STATUS.MANUALLY_BLOCKED, 2);
    assert.strictEqual(creditStatusLabel(0), 'Activo');
    assert.strictEqual(creditStatusLabel(1), 'Suspendido por Mora');
    assert.strictEqual(creditStatusLabel(2), 'Bloqueado Manualmente');
    assert.strictEqual(creditStatusLabel(9), 'Desconocido');
  });

  it('2. blocks missing, inactive, disabled, suspended, manually blocked and overdue profiles', () => {
    assert.strictEqual(isCreditProfileBlocked(null), true);
    assert.strictEqual(isCreditProfileBlocked({ ...healthyProfile, isActive: false }), true);
    assert.strictEqual(isCreditProfileBlocked({ ...healthyProfile, isCreditEnabled: false }), true);
    assert.strictEqual(isCreditProfileBlocked({ ...healthyProfile, creditStatus: CREDIT_STATUS.SUSPENDED_BY_MORA }), true);
    assert.strictEqual(isCreditProfileBlocked({ ...healthyProfile, creditStatus: CREDIT_STATUS.MANUALLY_BLOCKED }), true);
    assert.strictEqual(isCreditProfileBlocked({ ...healthyProfile, overdueCount: 1 }), true);
    assert.strictEqual(isCreditProfileBlocked(healthyProfile), false);
  });

  it('3. resolves the exact block message of the credit mode', () => {
    const message = (overrides = {}) => creditProfileBlockMessage({
      hasRegisteredCustomer: true,
      isLoading: false,
      error: null,
      profile: healthyProfile,
      ...overrides,
    });

    assert.match(message({ hasRegisteredCustomer: false }), /cliente registrado/);
    assert.match(message({ isLoading: true }), /Consultando el perfil crediticio/);
    assert.match(message({ error: 'No se pudo cargar el perfil crediticio: timeout' }), /timeout/);
    assert.match(message({ profile: null }), /No se pudo consultar el perfil crediticio/);
    assert.match(message({ profile: { ...healthyProfile, isActive: false } }), /inactivo/);
    assert.match(message({ profile: { ...healthyProfile, isCreditEnabled: false } }), /línea de crédito habilitada/);
    assert.match(message({ profile: { ...healthyProfile, creditStatus: CREDIT_STATUS.SUSPENDED_BY_MORA } }), /suspendido por mora/i);
    assert.match(message({ profile: { ...healthyProfile, creditStatus: CREDIT_STATUS.MANUALLY_BLOCKED } }), /bloqueado manualmente/);
    assert.match(message({ profile: { ...healthyProfile, overdueCount: 2 } }), /obligaciones vencidas/);
    assert.strictEqual(message(), null);
    assert.strictEqual(message({ hasRegisteredCustomer: true }), creditProfileBlockMessage({ hasRegisteredCustomer: true, isLoading: false, error: null, profile: healthyProfile }));
  });
});

describe('creditLogic — validación del perfil administrativo', () => {
  const valid = {
    isCreditEnabled: true,
    creditLimitUSD: 500,
    paymentTermDays: 30,
    creditGraceDays: 0,
    creditNotes: 'Notas',
    creditStatus: CREDIT_STATUS.ACTIVE,
  };

  it('4. accepts the valid manual states (Active / ManuallyBlocked)', () => {
    assert.deepStrictEqual(validateCreditProfileUpdate(valid), []);
    assert.deepStrictEqual(validateCreditProfileUpdate({ ...valid, creditStatus: CREDIT_STATUS.MANUALLY_BLOCKED }), []);
  });

  it('5. rejects negative limit, invalid term, negative grace and oversized notes', () => {
    const errors = (overrides) => validateCreditProfileUpdate({ ...valid, ...overrides });

    assert.ok(errors({ creditLimitUSD: -1 }).some((m) => /límite de crédito/i.test(m)));
    assert.ok(errors({ paymentTermDays: 0 }).some((m) => /1 y 365/.test(m)));
    assert.ok(errors({ paymentTermDays: 366 }).some((m) => /1 y 365/.test(m)));
    assert.ok(errors({ paymentTermDays: 2.5 }).some((m) => /1 y 365/.test(m)));
    assert.ok(errors({ creditGraceDays: -1 }).some((m) => /días de gracia/i.test(m)));
    assert.ok(errors({ creditNotes: 'x'.repeat(1001) }).some((m) => /1000 caracteres/.test(m)));
  });

  it('6. rejects the system-managed mora state and unknown states', () => {
    const mora = validateCreditProfileUpdate({ ...valid, creditStatus: CREDIT_STATUS.SUSPENDED_BY_MORA });
    assert.ok(mora.some((m) => /mora/i.test(m) && /sistema/i.test(m)));

    const unknown = validateCreditProfileUpdate({ ...valid, creditStatus: 99 });
    assert.ok(unknown.some((m) => /no es válido/i.test(m)));
  });
});

describe('creditLogic — checkout a crédito (total, mixto y sobregiro)', () => {
  const base = {
    hasRegisteredCustomer: true,
    isLoadingProfile: false,
    profileError: null,
    profile: healthyProfile,
    isPreviewFresh: true,
    previewRemainingUSD: 101.37,
    previewRemainingBsS: 3700.01,
  };

  it('7. takes the financed amount from the fresh server preview (crédito mixto)', () => {
    const state = computeCreditCheckoutState(base);

    assert.strictEqual(state.financedUSD, 101.37);
    assert.strictEqual(state.financedBsS, 3700.01);
    assert.strictEqual(state.isOverLimit, false);
    assert.strictEqual(state.overLimitMessage, null);
    assert.strictEqual(state.profileBlockMessage, null);
    assert.strictEqual(state.canFinalize, true);
  });

  it('8. falls back to the locally displayed remainder when the preview is not fresh (crédito total)', () => {
    const state = computeCreditCheckoutState({
      ...base,
      isPreviewFresh: false,
      previewRemainingUSD: 999,
      previewRemainingBsS: 999,
      fallbackFinancedUSD: 200,
      fallbackFinancedBsS: 7300,
    });

    assert.strictEqual(state.financedUSD, 200);
    assert.strictEqual(state.financedBsS, 7300);
    assert.strictEqual(state.canFinalize, true);
  });

  it('9. blocks the over-limit amount and keeps the +0.005 dust tolerance boundary', () => {
    const over = computeCreditCheckoutState({ ...base, previewRemainingUSD: 700.01, previewRemainingBsS: 25550 });
    assert.strictEqual(over.isOverLimit, true);
    assert.match(over.overLimitMessage, /autorización/i);
    assert.match(over.overLimitMessage, /WPF/i);
    assert.strictEqual(over.canFinalize, false);

    const onBoundary = computeCreditCheckoutState({ ...base, previewRemainingUSD: 700.005, previewRemainingBsS: 25550 });
    assert.strictEqual(onBoundary.isOverLimit, false);
    assert.strictEqual(onBoundary.canFinalize, true);
  });

  it('10. blocks finalization without a registered customer regardless of the amounts', () => {
    const state = computeCreditCheckoutState({ ...base, hasRegisteredCustomer: false });

    assert.match(state.profileBlockMessage, /cliente registrado/);
    assert.strictEqual(state.canFinalize, false);
  });

  it('11. blocks finalization while the profile is loading, failed or blocked', () => {
    assert.strictEqual(computeCreditCheckoutState({ ...base, isLoadingProfile: true }).canFinalize, false);
    assert.strictEqual(computeCreditCheckoutState({ ...base, profileError: 'boom' }).canFinalize, false);
    assert.strictEqual(computeCreditCheckoutState({ ...base, profile: null }).canFinalize, false);
    assert.strictEqual(
      computeCreditCheckoutState({ ...base, profile: { ...healthyProfile, creditStatus: CREDIT_STATUS.MANUALLY_BLOCKED } }).canFinalize,
      false
    );
  });

  it('12. builds the due date from the profile payment term and formats it for Venezuela', () => {
    const due = creditDueDate(30, new Date(2026, 9, 17)); // 17/10/2026 local
    assert.strictEqual(due.getFullYear(), 2026);
    assert.strictEqual(due.getMonth(), 10); // noviembre
    assert.strictEqual(due.getDate(), 16);
    assert.strictEqual(creditDueDate(0, new Date(2026, 9, 17)).getDate(), 17);
    assert.strictEqual(creditDueDate(NaN), null);

    assert.strictEqual(formatDateVe('2026-11-16T12:00:00Z'), '16/11/2026');
    // 02:00Z es el día anterior 22:00 en Caracas (-4): la fecha del comprobante no debe correrse.
    assert.strictEqual(formatDateVe('2026-11-16T02:00:00Z'), '15/11/2026');
    assert.strictEqual(formatDateVe(null), '—');
    assert.strictEqual(formatDateVe('nope'), '—');

    // El vencimiento es fecha de negocio local: se formatea sin conversión de zona horaria.
    assert.strictEqual(formatDueDateDisplay(creditDueDate(30, new Date(2026, 9, 17))), '16/11/2026');
    assert.strictEqual(formatDueDateDisplay(creditDueDate(0, new Date(2026, 9, 17))), '17/10/2026');
    assert.strictEqual(formatDueDateDisplay(null), '—');
    assert.strictEqual(formatDueDateDisplay('nope'), '—');
  });

  it('13. over-limit message is clear and routes the authorization to a WPF cash station', () => {
    const message = creditOverLimitMessage({ financedUSD: 200.5, availableCreditUSD: 50 });

    assert.match(message, /200/);
    assert.match(message, /50/);
    assert.match(message, /Administrador o Supervisor/);
    assert.match(message, /caja WPF/);
    assert.match(message, /no envía solicitudes de autorización/);
  });
});

describe('creditLogic — abono CxC (FIFO y facturas explícitas)', () => {
  const method = { id: 1, name: 'Efectivo', isCash: true, requiresReference: false };
  const transfer = { id: 2, name: 'Transferencia', isCash: false, requiresReference: true };
  const base = {
    amountUSD: 100,
    method,
    reference: '',
    isExplicitAllocation: false,
    selectedIds: [],
    selectedTotalUSD: 0,
    totalOpenUSD: 250,
  };

  it('14. accepts a valid FIFO payment within the customer open balance', () => {
    assert.strictEqual(validateCreditPaymentInput(base), null);
  });

  it('15. rejects zero amounts, missing method and missing required reference', () => {
    assert.match(validateCreditPaymentInput({ ...base, amountUSD: 0 }), /mayor a cero/);
    assert.match(validateCreditPaymentInput({ ...base, method: null }), /método de pago/);
    assert.match(
      validateCreditPaymentInput({ ...base, method: transfer, reference: '  ' }),
      /número de referencia/
    );
    assert.strictEqual(validateCreditPaymentInput({ ...base, method: transfer, reference: 'REF-9' }), null);
  });

  it('16. rejects FIFO amounts above the customer open balance (dust tolerance included)', () => {
    assert.strictEqual(validateCreditPaymentInput({ ...base, amountUSD: 250.005 }), null);
    assert.match(
      validateCreditPaymentInput({ ...base, amountUSD: 250.01 }),
      /excede el saldo pendiente/
    );
  });

  it('17. requires selected invoices in explicit mode and caps the amount to their balance', () => {
    assert.match(
      validateCreditPaymentInput({ ...base, isExplicitAllocation: true }),
      /al menos una factura/
    );

    assert.strictEqual(
      validateCreditPaymentInput({
        ...base,
        isExplicitAllocation: true,
        selectedIds: [11],
        selectedTotalUSD: 100,
        totalOpenUSD: 250,
      }),
      null
    );

    assert.match(
      validateCreditPaymentInput({
        ...base,
        isExplicitAllocation: true,
        selectedIds: [11],
        selectedTotalUSD: 90,
        totalOpenUSD: 250,
      }),
      /excede el saldo de las facturas seleccionadas/
    );
  });

  it('18. computes the informative Bs.S projection and the resulting balance for display only', () => {
    assert.strictEqual(receivableBsSProyeccion(100, 36.5), 3650);
    assert.strictEqual(receivableBsSProyeccion(10.005, 36.5), 365.18);
    assert.strictEqual(receivableBsSProyeccion(100, 0), 0);

    assert.strictEqual(creditPaymentRemaining(250, 100), 150);
    assert.strictEqual(creditPaymentRemaining(250, 300), 0);
  });

  it('18b. raises the carte mora alert only when the server reports overdue balances', () => {
    assert.strictEqual(hasOverdueAlert(0), false);
    assert.strictEqual(hasOverdueAlert(0.004), false);
    assert.strictEqual(hasOverdueAlert(120.5), true);
    assert.strictEqual(hasOverdueAlert(null), false);
    assert.strictEqual(hasOverdueAlert(undefined), false);
  });

  it('19. parses captured USD amounts allowing comma decimals and capping at 4dp', () => {
    assert.strictEqual(parseCreditAmount('120'), 120);
    assert.strictEqual(parseCreditAmount('120,5'), 120.5);
    assert.strictEqual(parseCreditAmount('1.234,56'), 1234.56);
    assert.strictEqual(parseCreditAmount('abc'), 0);
    assert.strictEqual(parseCreditAmount(''), 0);
    assert.strictEqual(parseCreditAmount('0.00005'), 0.0001);
  });
});

describe('creditLogic — cartera: estados y acciones por fila', () => {
  const openRow = { balanceUSD: 100, originalUSD: 100, status: 'Vigente' };

  it('20. exposes the canonical status filter options', () => {
    assert.deepStrictEqual(RECEIVABLE_STATUS_OPTIONS, ['Todos', 'Vigente', 'Parcial', 'Vencida', 'Pagada', 'Anulada']);
  });

  it('21. marks open obligations by balance > 0.005 and open status', () => {
    assert.strictEqual(isReceivableOpen(openRow), true);
    assert.strictEqual(isReceivableOpen({ ...openRow, status: 'Parcial' }), true);
    assert.strictEqual(isReceivableOpen({ ...openRow, status: 'Vencida' }), true);
    assert.strictEqual(isReceivableOpen({ ...openRow, status: 'Pagada' }), false);
    assert.strictEqual(isReceivableOpen({ ...openRow, status: 'Anulada' }), false);
    assert.strictEqual(isReceivableOpen({ ...openRow, balanceUSD: 0.004 }), false);
    assert.strictEqual(isReceivableOpen(null), false);
  });

  it('22. allows simple cancellation only without payments (balance == original)', () => {
    assert.strictEqual(isReceivableCancelable(openRow), true);
    assert.strictEqual(isReceivableCancelable({ ...openRow, balanceUSD: 90, status: 'Parcial' }), false);
    assert.strictEqual(isReceivableCancelable({ ...openRow, status: 'Pagada', balanceUSD: 0 }), false);
  });

  it('23. allows returns for open or paid obligations only', () => {
    assert.strictEqual(canReturnReceivable(openRow), true);
    assert.strictEqual(canReturnReceivable({ ...openRow, status: 'Pagada', balanceUSD: 0 }), true);
    assert.strictEqual(canReturnReceivable({ ...openRow, status: 'Anulada', balanceUSD: 0 }), false);
    assert.strictEqual(canReturnReceivable(null), false);
  });
});
