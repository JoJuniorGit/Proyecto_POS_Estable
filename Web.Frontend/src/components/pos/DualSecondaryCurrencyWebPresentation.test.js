// 8.211-T4b2b1 (S9): presentación Web de un perfil Dual con secundaria ISO no VES y de un
// perfil Single. Verifica las etiquetas de TopBar, carrito, resumen, checkout y pagos sin
// tocar montos, tasas ni la aritmética financiera.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import TopBar from '../layout/TopBar.jsx';
import CartTable from './CartTable.jsx';
import SummaryPanel from './SummaryPanel.jsx';
import PaymentForm, { resolveCashAmountRules } from '../checkout/PaymentForm.jsx';
import PaymentList from '../checkout/PaymentList.jsx';
import CheckoutModal from '../checkout/CheckoutModal.jsx';
import { CartProvider } from '../../context/CartContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';
import { AuthProvider } from '../../context/AuthContext';
import { ThemeProvider } from '../../context/ThemeContext';

const profileStore = new Map();
let cachedSale = null;

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
globalThis.sessionStorage = {
  getItem(key) {
    if (key === 'active_pos_sale_cache') {
      return cachedSale ? JSON.stringify(cachedSale) : null;
    }
    return null;
  },
  setItem() {},
  removeItem() {},
  clear() {},
};

function setProfile(profile) {
  profileStore.set('monetary_profile_client', JSON.stringify(profile));
}

function clearProfile() {
  profileStore.delete('monetary_profile_client');
}

function stripSsrComments(html) {
  return html.replace(/<!-- -->/g, '');
}

function withCartProviders(ui) {
  return stripSsrComments(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>
        <CartProvider>
          {ui}
        </CartProvider>
      </ExchangeRateProvider>
    </AuthProvider>
  ));
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

const pendingSale = {
  id: 31,
  status: 'Pending',
  items: [],
  subtotal: 6.3,
  totalUSD: 6.3,
  totalBsS: 315.35,
  appliedRate: 50,
  effectiveTotalUSD: 7.5,
};

const cartItem = {
  id: 1,
  productName: 'Harina PAN',
  quantity: 2,
  unitPrice: 10,
  unitPriceBsS: 874.5,
  subtotalBsS: 1749,
  isFractional: false,
  unitOfMeasure: 'Und',
};

function renderTopBar() {
  return stripSsrComments(renderToString(
    <ThemeProvider>
      <TopBar title="POS" exchangeRate={3600} isRateOutdated={false} onMenuClick={() => {}} />
    </ThemeProvider>
  ));
}

function renderCartTable() {
  return withCartProviders(
    <CartTable
      items={[cartItem]}
      selectedItemId={null}
      onSelectItem={() => {}}
      onUpdateQty={() => {}}
      onRemoveItem={() => {}}
    />
  );
}

function renderSummaryPanel() {
  cachedSale = pendingSale;
  return withCartProviders(<SummaryPanel onCheckout={() => {}} onHold={() => {}} />);
}

function renderCheckout() {
  cachedSale = pendingSale;
  return withCartProviders(
    <CheckoutModal isOpen onClose={() => {}} onSuccess={() => {}} />
  );
}

function renderPaymentForm() {
  return stripSsrComments(renderToString(
    <PaymentForm
      methods={[{ id: 1, name: 'Efectivo', isCash: true }]}
      remainingBsS={100}
      exchangeRate={3600}
      onAddPayment={() => {}}
    />
  ));
}

function renderPaymentList() {
  return stripSsrComments(renderToString(
    <PaymentList
      payments={[{ uid: 'p-1', methodName: 'Efectivo', amountBsS: 100, amountUsd: 4 }]}
      onRemovePayment={() => {}}
    />
  ));
}

describe('Dual USD/COP Web presentation [8.211-T4b2b1]', () => {
  beforeEach(() => {
    clearProfile();
    cachedSale = null;
    setProfile(dualUsdCop);
  });

  it('1. Shows COP labels in every touched POS/checkout surface and no hardcoded Bs.S', () => {
    const topBar = renderTopBar();
    const cart = renderCartTable();
    const summary = renderSummaryPanel();
    const form = renderPaymentForm();
    const list = renderPaymentList();
    const checkout = renderCheckout();

    assert.match(topBar, /COP 3\.600,00/);
    assert.match(cart, /Precio COP/);
    assert.match(cart, /COP 874,50/);
    assert.match(summary, /COP 315,35/);
    assert.match(summary, /\$ 7,50/);
    assert.match(form, /Monto \(COP\)/);
    assert.match(form, /Equivalente USD/);
    assert.match(list, /COP 100,00/);
    assert.match(list, /\(\$ 4,00\)/);
    assert.match(checkout, /COP 315,35/);
    assert.match(checkout, /Ref: \$ 7,50 USD/);

    for (const html of [topBar, cart, summary, form, list, checkout]) {
      assert.doesNotMatch(html, /Bs\.S/);
    }
  });
});

describe('Single COP Web presentation [8.211-T4b2b1]', () => {
  beforeEach(() => {
    clearProfile();
    cachedSale = null;
    setProfile(singleCop);
  });

  it('1. Shows only the primary currency and hides the rate chip, USD references and duplicates', () => {
    const topBar = renderTopBar();
    const cart = renderCartTable();
    const summary = renderSummaryPanel();
    const form = renderPaymentForm();
    const list = renderPaymentList();
    const checkout = renderCheckout();

    assert.match(cart, /COP 874,50/);
    assert.match(summary, /COP 315,35/);
    assert.doesNotMatch(summary, /summary-total-secondary/);
    assert.match(form, /Monto \(COP\)/);
    assert.doesNotMatch(form, /Equivalente/);
    assert.match(list, /COP 100,00/);
    assert.doesNotMatch(list, /payment-item-usd/);
    assert.match(checkout, /COP 315,35/);
    assert.doesNotMatch(checkout, /Ref:/);

    assert.doesNotMatch(topBar, /topbar-rate/);
    assert.doesNotMatch(topBar, /3\.600,00/);

    for (const html of [topBar, cart, summary, form, list, checkout]) {
      assert.doesNotMatch(html, /Bs\.S/);
      assert.doesNotMatch(html, /USD/);
    }
  });
});

describe('Dual USD/VES legacy presentation [8.211-T4b2b1]', () => {
  beforeEach(() => {
    clearProfile();
    cachedSale = null;
    setProfile(dualUsdVes);
  });

  it('1. Keeps Bs.S labels and the integer cash rule', () => {
    const topBar = renderTopBar();
    const cart = renderCartTable();
    const form = renderPaymentForm();
    const checkout = renderCheckout();

    assert.match(topBar, /Bs\.S 3\.600,00/);
    assert.match(cart, /Precio Bs\.S/);
    assert.match(form, /Monto \(Bs\.S\)/);
    assert.match(checkout, /Bs\.S 315,35/);
    assert.match(checkout, /Ref: \$ 7,50 USD/);

    assert.deepStrictEqual(
      resolveCashAmountRules({ mode: 'Dual', secondaryCurrencyCode: 'VES' }),
      { requiresIntegerCash: true, allowsFractionalCash: false }
    );
  });
});
