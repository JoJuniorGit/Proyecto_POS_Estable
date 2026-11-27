// 8.211-T4b2b2b (S9): crédito/CxC Web consume el perfil monetario activo. La deuda está
// denominada en la primaria (USD en Dual, primaria en Single) y la proyección local sólo se
// muestra en Dual. El perfil se inyecta por el cache local que usa el fallback del hook.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CreditPaymentModal from './CreditPaymentModal.jsx';
import { resolveCreditCurrencyLabels } from './CreditProfilePanel.jsx';
import { resolveCreditReturnCurrencyLabels } from './CreditReturnModal.jsx';
import { resolveCreditStatementCurrencyLabels } from './CreditStatementModal.jsx';
import CreditReceivablesPage from '../../pages/CreditReceivablesPage.jsx';
import { AuthProvider } from '../../context/AuthContext';

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

function renderPage() {
  return visibleText(renderToString(
    <AuthProvider>
      <CreditReceivablesPage />
    </AuthProvider>
  ));
}

function renderModal(Component, props) {
  return visibleText(renderToString(
    React.createElement(Component, { onClose: () => {}, ...props })
  ));
}

describe('8.211-T4b2b2b — credit currency labels', () => {
  beforeEach(() => storage.clear());

  it('1. resolves the local label as secondary in Dual and primary in Single', () => {
    assert.deepStrictEqual(resolveCreditCurrencyLabels(DUAL_USD_COP), {
      isDual: true,
      primaryLabel: 'USD',
      localLabel: 'COP',
    });
    assert.deepStrictEqual(resolveCreditCurrencyLabels(SINGLE_COP), {
      isDual: false,
      primaryLabel: 'COP',
      localLabel: 'COP',
    });
    assert.strictEqual(
      resolveCreditStatementCurrencyLabels(DUAL_USD_VES).localLabel,
      'Bs.S'
    );
    assert.strictEqual(
      resolveCreditReturnCurrencyLabels(DUAL_USD_COP).localLabel,
      'COP'
    );
  });

  it('2. CreditPaymentModal in Dual USD/COP shows primary USD and the COP projection', () => {
    useProfile(DUAL_USD_COP);
    const html = renderModal(CreditPaymentModal, {
      isOpen: true,
      customerId: 7,
      customerName: 'Ana Pérez',
      todayRate: 4000,
    });

    assert.match(html, /Monto del abono \(USD\)/);
    assert.match(html, /Equivalente COP/);
    assert.match(html, /Tasa del día: COP/);
    assert.match(html, /Saldo resultante del cliente/);
  });

  it('3. CreditPaymentModal in Single COP hides the secondary projection and the rate', () => {
    useProfile(SINGLE_COP);
    const html = renderModal(CreditPaymentModal, {
      isOpen: true,
      customerId: 7,
      customerName: 'Ana Pérez',
      todayRate: 1,
    });

    assert.match(html, /Monto del abono \(COP\)/);
    assert.doesNotMatch(html, /Equivalente/);
    assert.doesNotMatch(html, /Tasa del día/);
    assert.doesNotMatch(html, /Bs\.S/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
  });

  it('4. CreditReceivablesPage in Dual USD/COP labels the primary debt and the local rate', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage();

    assert.match(html, /deuda denominada en USD/);
    assert.match(html, /Tasa del día/);
    assert.match(html, /COP por USD/);
    assert.doesNotMatch(html, /Bs\.S por USD/);
  });

  it('5. CreditReceivablesPage in Single COP shows only COP and hides the rate metric', () => {
    useProfile(SINGLE_COP);
    const html = renderPage();

    assert.match(html, /deuda denominada en COP/);
    assert.doesNotMatch(html, /Tasa del día/);
    assert.doesNotMatch(html, /Bs\.S/);
    assert.doesNotMatch(html, /USD/);
  });

  it('6. CreditReceivablesPage in Dual USD/VES keeps the Bs.S and USD labels', () => {
    useProfile(DUAL_USD_VES);
    const html = renderPage();

    assert.match(html, /deuda denominada en USD/);
    assert.match(html, /Bs\.S por USD/);
    assert.match(html, /Tasa del día/);
  });
});
