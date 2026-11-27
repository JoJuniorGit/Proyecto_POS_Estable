// 8.211-T4b2b2b (S9): pedidos en espera y retiros pendientes Web consumen el perfil monetario
// activo. En `Dual` se muestran primaria + local; en `Single` sólo la primaria (sin línea USD
// duplicada ni columnas de tasa). El perfil se inyecta por el cache local del fallback del hook.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PendingOrderDesktopRow from './PendingOrderDesktopRow.jsx';
import PendingOrderMobileCard from './PendingOrderMobileCard.jsx';
import PendingOrdersPage, { resolvePendingOrdersCurrencyLabels } from '../../pages/PendingOrdersPage.jsx';
import PendingPickupsPage, { resolvePendingPickupsCurrencyLabels } from '../../pages/PendingPickupsPage.jsx';
import { getLockInfo } from '../../utils/holdLock';
import { buildPendingSale } from '../../../test/pendingSaleFixture';
import { AuthProvider } from '../../context/AuthContext';
import { ExchangeRateProvider } from '../../context/ExchangeRateContext';

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

function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function renderRow(overrides = {}) {
  const sale = buildPendingSale(overrides);
  return visibleText(renderToString(
    React.createElement(PendingOrderDesktopRow, {
      sale,
      isExpanded: true,
      lockInfo: getLockInfo(sale, 1),
      isElevated: false,
      onToggle: () => {},
      onCheckout: () => {},
      onEdit: () => {},
      onForceRelease: () => {},
    })
  ));
}

function renderCard(overrides = {}) {
  const sale = buildPendingSale(overrides);
  return visibleText(renderToString(
    React.createElement(PendingOrderMobileCard, {
      sale,
      isExpanded: true,
      lockInfo: getLockInfo(sale, 1),
      isElevated: false,
      exchangeRate: 847.44,
      onToggle: () => {},
      onCheckout: () => {},
      onEdit: () => {},
      onForceRelease: () => {},
    })
  ));
}

function renderPage(page) {
  return visibleText(renderToString(
    <AuthProvider>
      <ExchangeRateProvider>{page}</ExchangeRateProvider>
    </AuthProvider>
  ));
}

describe('8.211-T4b2b2b — pending currency labels', () => {
  beforeEach(() => storage.clear());

  it('1. resolves local labels as secondary in Dual and primary in Single', () => {
    assert.deepStrictEqual(resolvePendingOrdersCurrencyLabels(DUAL_USD_COP), {
      isDual: true,
      primaryLabel: 'USD',
      localLabel: 'COP',
    });
    assert.deepStrictEqual(resolvePendingPickupsCurrencyLabels(SINGLE_COP), {
      isDual: false,
      primaryLabel: 'COP',
      localLabel: 'COP',
    });
    assert.strictEqual(resolvePendingPickupsCurrencyLabels(DUAL_USD_VES).localLabel, 'Bs.S');
  });

  it('2. desktop row in Dual USD/COP labels the local amount and the primary abono', () => {
    useProfile(DUAL_USD_COP);
    const html = renderRow();

    assert.match(html, /Monto COP/);
    assert.match(html, /Abono USD/);
    assert.match(html, /Tasa Usada/);
    assert.match(html, /COP 1\.680,84/);
    assert.match(html, /\$ 2,00/);
  });

  it('3. desktop row in Single COP hides the USD column and the rate column', () => {
    useProfile(SINGLE_COP);
    const html = renderRow();

    assert.match(html, /Monto COP/);
    assert.match(html, /COP 1\.680,84/);
    assert.doesNotMatch(html, /Abono USD/);
    assert.doesNotMatch(html, /Tasa Usada/);
    assert.doesNotMatch(html, /Monto Bs\.S/);
    assert.doesNotMatch(html, /\$/);
  });

  it('4. mobile card in Dual USD/COP shows both totals and the local equivalence', () => {
    useProfile(DUAL_USD_COP);
    const html = renderCard();

    assert.match(html, /Total Factura/);
    assert.match(html, /COP 1\.680,84/);
    assert.match(html, /\$ 2,00/);
    assert.match(html, /Abonado/);
    assert.match(html, /Deuda Pendiente/);
    assert.match(html, /≈ COP/);
    assert.match(html, /\+\$ 0,59/);
  });

  it('5. mobile card in Single COP hides the primary duplicate and the equivalence line', () => {
    useProfile(SINGLE_COP);
    const html = renderCard();

    assert.match(html, /Total Factura/);
    assert.match(html, /COP 1\.680,84/);
    assert.match(html, /Abonado/);
    assert.match(html, /Deuda Pendiente/);
    assert.doesNotMatch(html, /≈/);
    assert.doesNotMatch(html, /Total \(Bs\.S\)/);
    assert.doesNotMatch(html, /\$/);
  });

  it('6. PendingOrdersPage in Dual USD/COP labels the BCV badge with the local pair', () => {
    useProfile(DUAL_USD_COP);
    const html = renderPage(<PendingOrdersPage />);

    assert.match(html, /Tasa BCV del Día/);
    assert.match(html, /COP\/USD/);
    assert.match(html, /abonos en COP/);
  });

  it('7. PendingOrdersPage in Single COP hides the rate badge and USD labels', () => {
    useProfile(SINGLE_COP);
    const html = renderPage(<PendingOrdersPage />);

    assert.doesNotMatch(html, /Tasa BCV del Día/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /\$/);
    assert.match(html, /abonos en COP/);
  });

  it('8. pickups page renders under both profiles without legacy Bs.S labels', () => {
    useProfile(DUAL_USD_COP);
    const dualHtml = renderPage(<PendingPickupsPage />);
    assert.doesNotMatch(dualHtml, /Bs\.S/);

    useProfile(SINGLE_COP);
    const singleHtml = renderPage(<PendingPickupsPage />);
    assert.doesNotMatch(singleHtml, /USD/);
    assert.doesNotMatch(singleHtml, /\$/);
  });
});
