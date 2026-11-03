import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import SuggestionList from './SuggestionList.jsx';

/**
 * 8.186-T2 (S1/S3, web): la grilla de resultados de búsqueda del POS muestra el Bs.S EFECTIVO
 * (`effectivePriceBsS`) proyectado por el servidor con el motor único; el canónico USD × tasa
 * (8.143) queda solo como fallback documentado cuando el campo falta o viene <= 0.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'SuggestionList.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

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

function renderSuggestion(item, exchangeRate = 50) {
  return renderToString(
    <SuggestionList
      suggestions={[item]}
      isLoading={false}
      onSelectSuggestion={() => {}}
      exchangeRate={exchangeRate}
    />
  ).replace(/<!-- -->/g, '');
}

describe('SuggestionList — consume el efectivo del servidor (8.186-T2, S1/S3)', () => {
  it('1. Renderiza effectivePriceBsS aunque el canónico USD × tasa difiera', () => {
    const html = renderSuggestion({
      id: 1,
      name: 'Harina PAN',
      sku: 'HP1',
      priceUSD: 10,
      priceBsS: 100,
      effectivePriceBsS: 874.5,
    });
    assert.ok(html.includes('874,50'), html);
    assert.ok(!html.includes('500,00'), 'no debe renderizar el canónico USD × tasa');
  });

  it('2. Fallback canónico documentado cuando el campo efectivo falta', () => {
    const html = renderSuggestion({ id: 2, name: 'Arroz', sku: 'AR1', priceUSD: 10, priceBsS: 100 });
    assert.ok(html.includes('500,00'), html);
  });

  it('3. Fallback canónico documentado cuando el campo efectivo viene 0', () => {
    const html = renderSuggestion({ id: 3, name: 'Café', sku: 'CA1', priceUSD: 10, priceBsS: 100, effectivePriceBsS: 0 });
    assert.ok(html.includes('500,00'), html);
  });

  it('4. Sin USD ni tasa válidos el fallback mantiene el snapshot persistido', () => {
    const html = renderSuggestion({ id: 4, name: 'Azúcar', sku: 'AZ1', priceUSD: 0, priceBsS: 300 }, 0);
    assert.ok(html.includes('300,00'), html);
  });

  it('5. La fuente prioriza el campo del servidor y no implementa política de redondeo (S3)', () => {
    assert.ok(
      normalizedSource.includes(
        'resolveEffectiveBsS( item.effectivePriceBsS, resolveBsSPrice(item.priceUSD, item.priceBsS, exchangeRate) );'
      ),
      normalizedSource
    );
    assert.ok(!source.includes('Math.ceil'), 'sin matemática de política en JS (S3)');
  });
});

describe('SuggestionList monetary profile labels [8.211-T4b2b1]', () => {
  beforeEach(clearProfile);

  it('1. Dual USD/COP labels the effective price with COP and hides Bs.S', () => {
    setProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'COP',
      secondaryCurrencySymbol: 'COP',
      isConfigured: true,
    });

    const html = renderSuggestion({
      id: 11,
      name: 'Harina PAN',
      sku: 'HP1',
      priceUSD: 10,
      priceBsS: 100,
      effectivePriceBsS: 874.5,
    });

    assert.ok(html.includes('COP'), html);
    assert.ok(html.includes('874,50'), html);
    assert.ok(!html.includes('Bs.S'), html);
  });

  it('2. Single COP uses only the primary label', () => {
    setProfile({
      mode: 'Single',
      primaryCurrencyCode: 'COP',
      primaryCurrencySymbol: 'COP',
      secondaryCurrencyCode: null,
      secondaryCurrencySymbol: null,
      isConfigured: true,
    });

    const html = renderSuggestion({
      id: 12,
      name: 'Arroz',
      sku: 'AR1',
      priceUSD: 10,
      priceBsS: 100,
      effectivePriceBsS: 500,
    });

    assert.ok(html.includes('COP'), html);
    assert.ok(html.includes('500,00'), html);
    assert.ok(!html.includes('Bs.S'), html);
  });
});
