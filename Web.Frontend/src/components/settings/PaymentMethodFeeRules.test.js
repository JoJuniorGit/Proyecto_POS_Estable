// F4-WU2 (Paso 14): tarjeta Admin-only de reglas de comisión dentro de "Métodos de Pago y Caja".
// El runner no tiene DOM: la visibilidad por rol se valida con SSR y las reglas de validación
// con funciones puras exportadas por el componente.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PaymentMethodFeeRules, {
  buildFeeRulePayload,
  canManageFeeRules,
  formatFeeRuleDate,
  formatFeeRuleValue,
  showFeeFixedAmount,
  showFeePercentage,
  validateFeeRuleForm,
} from './PaymentMethodFeeRules.jsx';
import { AuthProvider } from '../../context/AuthContext';

let profile = null;

globalThis.sessionStorage = {
  getItem: () => (profile ? JSON.stringify(profile) : null),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const METHODS = [
  { id: 4, name: 'Punto de Venta', isCash: false, isDeleted: false },
  { id: 5, name: 'Pago Móvil', isCash: false, isDeleted: false },
  { id: 6, name: 'Efectivo Bs.S', isCash: true, isDeleted: false },
];

const VALID_FORM = {
  paymentMethodId: 4,
  feeType: 0,
  percentage: '1,5',
  fixedAmount: '',
  currencyCode: 'USD',
  effectiveFrom: '2026-11-01',
  effectiveTo: '',
  isActive: true,
};

function renderCard(role) {
  profile = role === undefined ? null : { id: 1, name: 'Test User', cedula: 'V-1', role };
  return renderToString(
    <AuthProvider>
      <PaymentMethodFeeRules methods={METHODS} />
    </AuthProvider>
  );
}

describe('PaymentMethodFeeRules — Admin-only visibility', () => {
  it('1. only Admin can manage fee rules (canonical and numeric aliases)', () => {
    assert.strictEqual(canManageFeeRules('Admin'), true);
    assert.strictEqual(canManageFeeRules('admin'), true);
    assert.strictEqual(canManageFeeRules(3), true);
    assert.strictEqual(canManageFeeRules('Manager'), false);
    assert.strictEqual(canManageFeeRules(2), false);
    assert.strictEqual(canManageFeeRules('Cashier'), false);
    assert.strictEqual(canManageFeeRules(1), false);
    assert.strictEqual(canManageFeeRules(null), false);
  });

  it('2. Admin sees the card and its mutation controls', () => {
    const html = renderCard('Admin');

    assert.match(html, /Comisiones de Medios de Pago/);
    assert.match(html, /Costo del negocio por cobros electrónicos/);
    assert.match(html, /Nueva Regla/);
    assert.match(html, /Método/);
    assert.match(html, /Tipo/);
    assert.match(html, /Valor/);
    assert.match(html, /Moneda/);
    assert.match(html, /Estado/);
  });

  it('3. Manager and Cashier never see the card or mutation controls', () => {
    for (const role of ['Manager', 2, 'Cashier', 1]) {
      const html = renderCard(role);
      assert.doesNotMatch(html, /Comisiones de Medios de Pago/, `${role} must not see the card`);
      assert.doesNotMatch(html, /Nueva Regla/, `${role} must not see mutation controls`);
    }
  });
});

describe('PaymentMethodFeeRules — form validation', () => {
  it('4. accepts a valid percentage rule', () => {
    assert.strictEqual(validateFeeRuleForm(VALID_FORM), null);
  });

  it('5. rejects a percentage outside (0, 100]', () => {
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, percentage: '0' }),
      /El porcentaje de comisión debe ser mayor que cero y menor o igual a 100\./
    );
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, percentage: '100,01' }),
      /El porcentaje de comisión debe ser mayor que cero y menor o igual a 100\./
    );
  });

  it('6. rejects invalid fixed amounts per fee type', () => {
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, feeType: 1, percentage: '', fixedAmount: '0' }),
      /El monto fijo de comisión debe ser mayor que cero\./
    );
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, feeType: 2, fixedAmount: '-1' }),
      /El monto fijo de comisión no puede ser negativo\./
    );
  });

  it('7. rejects a missing currency, missing start date and inverted range', () => {
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, currencyCode: '  ' }),
      /La moneda de la comisión es obligatoria\./
    );
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, effectiveFrom: '' }),
      /La fecha de inicio de vigencia es obligatoria\./
    );
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, effectiveTo: '2026-11-01' }),
      /La fecha de inicio de vigencia debe ser anterior a la fecha de fin de vigencia\./
    );
  });

  it('8. requires a payment method only when creating a rule', () => {
    assert.match(
      validateFeeRuleForm({ ...VALID_FORM, paymentMethodId: '' }),
      /Seleccione un método de pago para la regla de comisión\./
    );
    assert.strictEqual(
      validateFeeRuleForm(
        { ...VALID_FORM, paymentMethodId: '' },
        { requirePaymentMethod: false }
      ),
      null
    );
  });

  it('9. exposes the conditional fields per fee type', () => {
    assert.strictEqual(showFeePercentage(0), true);
    assert.strictEqual(showFeePercentage(1), false);
    assert.strictEqual(showFeePercentage(2), true);
    assert.strictEqual(showFeeFixedAmount(0), false);
    assert.strictEqual(showFeeFixedAmount(1), true);
    assert.strictEqual(showFeeFixedAmount(2), true);
  });
});

describe('PaymentMethodFeeRules — payloads and display', () => {
  it('10. builds the create payload with the payment method and UTC dates', () => {
    const payload = buildFeeRulePayload(
      { ...VALID_FORM, feeType: 2, percentage: '1,5', fixedAmount: '0,30' },
      { includePaymentMethod: true }
    );

    assert.deepStrictEqual(payload, {
      paymentMethodId: 4,
      feeType: 2,
      percentage: 1.5,
      fixedAmount: 0.3,
      currencyCode: 'USD',
      effectiveFromUtc: '2026-11-01T00:00:00.000Z',
      effectiveToUtc: null,
      isActive: true,
    });
  });

  it('11. builds the update payload without a payment method id', () => {
    const payload = buildFeeRulePayload(VALID_FORM, { includePaymentMethod: false });

    assert.strictEqual('paymentMethodId' in payload, false);
    assert.strictEqual(payload.percentage, 1.5);
    assert.strictEqual(payload.fixedAmount, null);
  });

  it('12. formats the fee value by type and the effective dates deterministically', () => {
    assert.strictEqual(formatFeeRuleValue({ feeType: 0, percentage: 1.5 }), '1,5 %');
    assert.strictEqual(formatFeeRuleValue({ feeType: 1, fixedAmount: 0.3 }), '0,30');
    assert.strictEqual(
      formatFeeRuleValue({ feeType: 2, percentage: 1.5, fixedAmount: 0.3 }),
      '1,5 % + 0,30'
    );
    assert.strictEqual(formatFeeRuleDate('2026-11-01T00:00:00Z'), '01/11/2026');
    assert.strictEqual(formatFeeRuleDate(null), '—');
  });
});
