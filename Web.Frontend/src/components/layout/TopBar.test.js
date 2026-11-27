// 8.211-T4b2b1 (S9): el chip de tasa del TopBar presenta la moneda secundaria del perfil en
// Dual y se oculta en Single (el régimen identidad no tiene una tasa USD/VES que mostrar).
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import TopBar from './TopBar.jsx';
import { ThemeProvider } from '../../context/ThemeContext';

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

function stripSsrComments(html) {
  return html.replace(/<!-- -->/g, '');
}

function renderTopBar(overrides = {}) {
  return stripSsrComments(renderToString(
    <ThemeProvider>
      <TopBar
        title="Punto de Venta"
        exchangeRate={3600}
        isRateOutdated={false}
        onMenuClick={() => {}}
        {...overrides}
      />
    </ThemeProvider>
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

describe('TopBar monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP labels the rate chip with the secondary currency', () => {
    setProfile(dualUsdCop);
    const html = renderTopBar();

    assert.match(html, /COP 3\.600,00/);
    assert.doesNotMatch(html, /Bs\.S/);
  });

  it('2. Dual USD/VES keeps the Bs.S label', () => {
    setProfile(dualUsdVes);
    const html = renderTopBar();

    assert.match(html, /Bs\.S 3\.600,00/);
    assert.doesNotMatch(html, /COP/);
  });

  it('3. Single hides the exchange-rate chip entirely', () => {
    setProfile(singleCop);
    const html = renderTopBar();

    assert.doesNotMatch(html, /topbar-rate/);
    assert.doesNotMatch(html, /3\.600,00/);
  });

  it('4. Keeps the outdated-rate tooltip and warning icon', () => {
    setProfile(dualUsdCop);
    const html = renderTopBar({ isRateOutdated: true });

    assert.match(html, /rate-outdated/);
    assert.match(html, /Tasa no actualizada en más de 24 horas/);
    assert.match(html, /Tasa desactualizada \(&gt;24h\)/);
  });
});
