// 8.211-T4b2b2a (S9): historial, caja/cierres, adelantos y caja chica Web consumen el perfil
// monetario activo. El perfil se inyecta por el cache local que usa el fallback del hook (sin
// provider) para poder renderizar en SSR con un régimen Dual USD/COP y Single COP.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import HistoryPage from '../../pages/HistoryPage.jsx';
import RegisterPage from '../../pages/RegisterPage.jsx';
import CashDrawerOpenModal from './CashDrawerOpenModal.jsx';
import CashDrawerCloseModal from './CashDrawerCloseModal.jsx';
import CashInModal from './CashInModal.jsx';
import CashOutModal from './CashOutModal.jsx';
import CashAdvanceModal from './CashAdvanceModal.jsx';
import PettyCashExpenseModal from './PettyCashExpenseModal.jsx';
import { resolveLocalCurrencyProfile, profileIncludesCurrency } from './registerFormLogic.js';
import { resolveCashAmountRules } from '../checkout/PaymentForm.jsx';
import { AuthProvider } from '../../context/AuthContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthorizationProvider } from '../authorization/AuthorizationProvider.jsx';

const storage = new Map();
const localStorageMock = {
  getItem: (key) => (storage.has(key) ? storage.get(key) : null),
  setItem: (key, value) => storage.set(key, String(value)),
  removeItem: (key) => storage.delete(key),
  clear: () => storage.clear(),
};

globalThis.localStorage = localStorageMock;
globalThis.window = { localStorage: localStorageMock };
globalThis.sessionStorage = {
  getItem: () => JSON.stringify({ id: 1, name: 'Admin Test', cedula: 'V-1', role: 'Admin' }),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const DUAL_USD_COP = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'COP',
  secondaryCurrencySymbol: 'COP',
  isConfigured: true,
};

const DUAL_USD_VES = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: true,
};

const SINGLE_COP = {
  mode: 'Single',
  primaryCurrencyCode: 'COP',
  primaryCurrencySymbol: 'COP',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
  isConfigured: true,
};

function useProfile(profile) {
  storage.set('monetary_profile_client', JSON.stringify(profile));
}

// React intercala marcadores de comentario entre nodos de texto en SSR; las aserciones de
// etiquetas deben evaluar el texto visible, no los separadores del renderer.
function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function renderWithProviders(node) {
  return visibleText(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <AuthorizationProvider>{node}</AuthorizationProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  ));
}

function renderPage(page) {
  return renderWithProviders(page);
}

function renderCloseModal(overrides = {}) {
  return visibleText(renderToString(
    React.createElement(CashDrawerCloseModal, {
      isOpen: true,
      onClose: () => {},
      sessionId: 42,
      exchangeRate: 400,
      expectedCashBsS: 1250.5,
      expectedCashUsd: 20,
      role: 'Admin',
      onSuccess: () => {},
      ...overrides,
    })
  ));
}

function renderOpenModal(overrides = {}) {
  return visibleText(renderToString(
    React.createElement(CashDrawerOpenModal, {
      isOpen: true,
      onClose: () => {},
      exchangeRate: 400,
      onSuccess: () => {},
      ...overrides,
    })
  ));
}

function renderPettyCashExpense() {
  return visibleText(renderToString(
    React.createElement(
      AuthorizationProvider,
      null,
      React.createElement(PettyCashExpenseModal, {
        isOpen: true,
        onClose: () => {},
        sessionId: 42,
        exchangeRate: 400,
        role: 'Admin',
        onSuccess: () => {},
      })
    )
  ));
}

describe('8.211-T4b2b2a — local currency resolver', () => {
  beforeEach(() => storage.clear());

  it('1. resolves the secondary currency as local in Dual and the primary in Single', () => {
    assert.deepStrictEqual(
      resolveLocalCurrencyProfile(DUAL_USD_COP),
      { isDual: true, code: 'COP', label: 'COP', symbol: 'COP' }
    );
    assert.deepStrictEqual(
      resolveLocalCurrencyProfile(SINGLE_COP),
      { isDual: false, code: 'COP', label: 'COP', symbol: 'COP' }
    );
    assert.strictEqual(resolveLocalCurrencyProfile(DUAL_USD_VES).label, 'Bs.S');
  });

  it('2. detects whether USD belongs to the active profile', () => {
    assert.strictEqual(profileIncludesCurrency(DUAL_USD_COP, 'USD'), true);
    assert.strictEqual(profileIncludesCurrency(SINGLE_COP, 'USD'), false);
    assert.strictEqual(profileIncludesCurrency(DUAL_USD_VES, 'USD'), true);
  });

  it('3. mirror the backend cash rule: only Dual USD/VES requires integer local cash', () => {
    assert.strictEqual(
      resolveCashAmountRules({ mode: 'Dual', secondaryCurrencyCode: 'VES' }).requiresIntegerCash,
      true
    );
    assert.strictEqual(
      resolveCashAmountRules({ mode: 'Dual', secondaryCurrencyCode: 'COP' }).requiresIntegerCash,
      false
    );
    assert.strictEqual(
      resolveCashAmountRules({ mode: 'Single', secondaryCurrencyCode: null }).requiresIntegerCash,
      false
    );
  });
});

describe('8.211-T4b2b2a — RegisterPage and HistoryPage labels', () => {
  beforeEach(() => storage.clear());

  it('4. RegisterPage in Dual USD/COP keeps USD primary and shows COP local labels', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage(<RegisterPage />);

    assert.match(html, /EFECTIVO ESPERADO EN CAJA/);
    assert.match(html, /COP/);
    assert.match(html, /Tasa Actual: COP [\d.,]+ \/ USD/);
  });

  it('5. RegisterPage in Single COP hides the USD equivalence and shows only COP', () => {
    useProfile(SINGLE_COP);
    const html = renderPage(<RegisterPage />);

    assert.match(html, /COP/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
    assert.doesNotMatch(html, /Tasa Actual/);
  });

  it('6. HistoryPage renders with a Dual USD/COP profile without legacy Bs.S labels', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage(<HistoryPage />);

    assert.match(html, /Historial de Ventas/);
    assert.doesNotMatch(html, /Total Bs\.S/);
    assert.doesNotMatch(html, /Total USD/);
  });
});

describe('8.211-T4b2b2a — cash drawer and petty cash labels', () => {
  beforeEach(() => storage.clear());

  it('7. CashDrawerCloseModal in Dual USD/COP uses COP for the local count and keeps USD', () => {
    useProfile(DUAL_USD_COP);
    const html = renderCloseModal();

    assert.match(html, /Efectivo esperado COP:/);
    assert.match(html, /Efectivo COP contado/);
    assert.match(html, /Efectivo USD contado \(opcional\)/);
    assert.match(html, /Tasa vigente: COP [\d.,]+ \/ USD/);
  });

  it('8. CashDrawerCloseModal in Single COP hides the USD count, equivalence and rate', () => {
    useProfile(SINGLE_COP);
    const html = renderCloseModal();

    assert.match(html, /Efectivo COP contado/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
    assert.doesNotMatch(html, /Tasa vigente/);
  });

  it('9. CashDrawerOpenModal in Dual USD/COP labels both drawers with the profile', () => {
    useProfile(DUAL_USD_COP);
    const html = renderOpenModal();

    assert.match(html, /Fondo inicial COP/);
    assert.match(html, /Fondo inicial USD/);
    assert.match(html, /Tasa aplicada: COP [\d.,]+ \/ USD/);
  });

  it('10. CashDrawerOpenModal in Single COP hides the USD drawer', () => {
    useProfile(SINGLE_COP);
    const html = renderOpenModal();

    assert.match(html, /Fondo inicial COP/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
  });

  it('11. PettyCashExpenseModal offers the COP local option and hides USD in Single COP', () => {
    useProfile(DUAL_USD_COP);
    const dualHtml = renderPettyCashExpense();
    assert.match(dualHtml, /COP \(moneda local\)/);
    assert.match(dualHtml, /USD/);

    useProfile(SINGLE_COP);
    const singleHtml = renderPettyCashExpense();
    assert.match(singleHtml, /COP \(moneda local\)/);
    assert.doesNotMatch(singleHtml, /USD/);
    assert.doesNotMatch(singleHtml, /\$/);
  });

  it('12. Dual USD/VES keeps Bs.S local labels and USD primary', () => {
    useProfile(DUAL_USD_VES);
    const html = renderCloseModal();

    assert.match(html, /Efectivo esperado Bs\.S:/);
    assert.match(html, /Efectivo Bs\.S contado/);
    assert.match(html, /Efectivo USD contado \(opcional\)/);
  });
});

describe('8.211-T4b2b2a — cash input precision parity', () => {
  beforeEach(() => storage.clear());

  function renderCashModal(Component, overrides = {}) {
    return visibleText(renderToString(
      React.createElement(Component, {
        isOpen: true,
        onClose: () => {},
        sessionId: 1,
        exchangeRate: 50,
        user: { id: 1, name: 'Admin' },
        availableCashBsS: 2000,
        onSuccess: () => {},
        ...overrides,
      })
    ));
  }

  it('13. Dual USD/VES keeps the integer-only local cash input', () => {
    useProfile(DUAL_USD_VES);

    for (const Component of [CashInModal, CashOutModal, CashAdvanceModal]) {
      const html = renderCashModal(Component);
      assert.match(html, /step="1"/);
      assert.match(html, /inputMode="numeric"/);
      assert.match(html, /Solo Enteros/);
    }
  });

  it('14. Single COP accepts fractional local cash with the COP label', () => {
    useProfile(SINGLE_COP);

    for (const Component of [CashInModal, CashOutModal, CashAdvanceModal]) {
      const html = renderCashModal(Component);
      assert.match(html, /step="0\.01"/);
      assert.match(html, /inputMode="decimal"/);
      assert.match(html, /COP/);
    }
  });
});
