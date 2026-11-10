// 8.211-T4b2b1 (S9): el formulario de pago usa la etiqueta local del perfil, muestra la
// equivalencia USD solo cuando el USD pertenece al perfil (Dual) y alinea la validación de
// efectivo con el backend: Dual USD/VES exige enteros; Single y Dual no-VES aceptan centavos.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PaymentForm, { resolveCashAmountRules } from './PaymentForm.jsx';

const profileStore = new Map();
globalThis.window = {
  localStorage: {
    getItem(key) {
      return profileStore.has(key) ? profileStore.get(key) : null;
    },
    setItem(key, value) {
      profileStore.set(key, value);
    },
    removeItem(key) {
      profileStore.delete(key);
    },
  },
};

function setProfile(profile) {
  profileStore.set('monetary_profile_client', JSON.stringify(profile));
}

function clearProfile() {
  profileStore.delete('monetary_profile_client');
}

function renderPaymentForm() {
  return renderToString(
    <PaymentForm
      methods={[{ id: 1, name: 'Efectivo', isCash: true }]}
      remainingBsS={100}
      exchangeRate={3600}
      onAddPayment={() => {}}
    />
  ).replace(/<!-- -->/g, '');
}

const dualUsdCop = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'COP',
  secondaryCurrencySymbol: 'COP',
  isConfigured: true,
};

const dualUsdVes = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

const singleCop = {
  mode: 'Single',
  primaryCurrencyCode: 'COP',
  primaryCurrencySymbol: 'COP',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
  isConfigured: true,
};

describe('PaymentForm monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP labels the amount in COP and keeps the USD equivalent', () => {
    setProfile(dualUsdCop);
    const html = renderPaymentForm();

    assert.match(html, /Monto \(COP\)/);
    assert.match(html, /Equivalente USD/);
    assert.match(html, /USD/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Single COP labels the amount in COP and hides the USD equivalent', () => {
    setProfile(singleCop);
    const html = renderPaymentForm();

    assert.match(html, /Monto \(COP\)/);
    assert.doesNotMatch(html, /Equivalente/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('3. Dual USD/VES keeps the Bs.S local label and the USD equivalent', () => {
    setProfile(dualUsdVes);
    const html = renderPaymentForm();

    assert.match(html, /Monto \(Bs\.S\)/);
    assert.match(html, /Equivalente USD/);
  });
});

describe('PaymentForm cash validation rules [8.211-T4b2b1]', () => {
  it('1. Dual USD/VES keeps the integer cash rule', () => {
    assert.deepStrictEqual(
      resolveCashAmountRules({ mode: 'Dual', secondaryCurrencyCode: 'VES' }),
      { requiresIntegerCash: true, allowsFractionalCash: false }
    );
  });

  it('2. Single accepts fractional cash regardless of the primary currency', () => {
    assert.deepStrictEqual(
      resolveCashAmountRules({ mode: 'Single', secondaryCurrencyCode: null }),
      { requiresIntegerCash: false, allowsFractionalCash: true }
    );
  });

  it('3. Dual with a non-VES secondary accepts fractional cash', () => {
    assert.deepStrictEqual(
      resolveCashAmountRules({ mode: 'Dual', secondaryCurrencyCode: 'COP' }),
      { requiresIntegerCash: false, allowsFractionalCash: true }
    );
  });
});
