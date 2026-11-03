// 8.211-T4b2b1 (S9): el hold web presenta la moneda local/secundaria del perfil en sus
// etiquetas y oculta la referencia primaria en Single. El payload y las cuotas no cambian.
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import HoldSaleModal from './HoldSaleModal.jsx';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'HoldSaleModal.jsx'), 'utf-8');

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

function renderHoldSale() {
  return renderToString(
    <HoldSaleModal
      isOpen
      onClose={() => {}}
      saleId={7}
      currentCustomer={null}
      saleTotalUSD={100}
      saleTotalBsS={5000}
      exchangeRate={50}
      onSuccess={() => {}}
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

describe('HoldSaleModal monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP shows the COP total and the primary reference', () => {
    setProfile(dualUsdCop);
    const html = renderHoldSale();

    assert.match(html, /COP 5\.000,00/);
    assert.match(html, /Ref: \$ 100,00/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Dual USD/VES keeps the Bs.S total and the primary reference', () => {
    setProfile(dualUsdVes);
    const html = renderHoldSale();

    assert.match(html, /Bs\.S 5\.000,00/);
    assert.match(html, /Ref: \$ 100,00/);
  });

  it('3. Single COP shows a single primary total and no reference line', () => {
    setProfile(singleCop);
    const html = renderHoldSale();

    assert.match(html, /COP 5\.000,00/);
    assert.doesNotMatch(html, /Ref:/);
    assert.doesNotMatch(html, /USD/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('4. The payment label and rate line use profile labels instead of Bs.S/Bs/$', () => {
    assert.match(source, /Monto de Abono \(\{localCurrencySymbol\}\)/);
    assert.ok(!source.includes('Bs/$'), 'hardcoded Bs/$ label must be removed');
    assert.ok(!source.includes('Monto de Abono (Bs.S)'), 'hardcoded Bs.S label must be removed');
  });
});
