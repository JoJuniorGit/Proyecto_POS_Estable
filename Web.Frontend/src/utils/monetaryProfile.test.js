// 8.211-T4b1 (S9): contrato puro del perfil monetario Web. Normaliza el DTO del backend,
// resuelve símbolos y compone el texto delegando la aritmética/formato en formatAmount.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  DEFAULT_MONETARY_PROFILE,
  displayCurrencyCode,
  formatWithSymbol,
  normalizeCurrencyCode,
  normalizeMonetaryProfile,
  symbolForCode,
} from './monetaryProfile.js';

describe('monetaryProfile — defaults and normalization', () => {
  it('1. default profile is the legacy-compatible Dual USD/VES presentation', () => {
    assert.deepStrictEqual(DEFAULT_MONETARY_PROFILE, {
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
      isConfigured: false,
    });
    assert.deepStrictEqual(normalizeMonetaryProfile(undefined), DEFAULT_MONETARY_PROFILE);
    assert.deepStrictEqual(normalizeMonetaryProfile(null), DEFAULT_MONETARY_PROFILE);
  });

  it('2. malformed or partial payloads never throw and fall back field by field', () => {
    const normalized = normalizeMonetaryProfile({
      mode: 'nope',
      primaryCurrencyCode: '??',
      primaryCurrencySymbol: '   ',
      secondaryCurrencyCode: 7,
      secondaryCurrencySymbol: null,
      isConfigured: 'yes',
    });

    assert.strictEqual(normalized.mode, 'Dual');
    assert.strictEqual(normalized.primaryCurrencyCode, 'USD');
    assert.strictEqual(normalized.primaryCurrencySymbol, '$');
    assert.strictEqual(normalized.secondaryCurrencyCode, 'VES');
    assert.strictEqual(normalized.secondaryCurrencySymbol, 'Bs.S');
    assert.strictEqual(normalized.isConfigured, false);

    for (const value of [null, undefined, 'text', 42, [], () => {}]) {
      assert.doesNotThrow(() => normalizeMonetaryProfile(value));
    }
  });

  it('3. Single drops the secondary currency and keeps its configured primary symbol', () => {
    const normalized = normalizeMonetaryProfile({
      mode: 0,
      primaryCurrencyCode: 'eur',
      primaryCurrencySymbol: '€',
      secondaryCurrencyCode: 'USD',
      secondaryCurrencySymbol: '$',
      isConfigured: true,
    });

    assert.strictEqual(normalized.mode, 'Single');
    assert.strictEqual(normalized.primaryCurrencyCode, 'EUR');
    assert.strictEqual(normalized.primaryCurrencySymbol, '€');
    assert.strictEqual(normalized.secondaryCurrencyCode, null);
    assert.strictEqual(normalized.secondaryCurrencySymbol, null);
    assert.strictEqual(normalized.isConfigured, true);
  });

  it('4. Dual accepts the string mode and canonicalizes VES aliases while presenting Bs.S', () => {
    const normalized = normalizeMonetaryProfile({
      mode: 'dual',
      primaryCurrencyCode: 'usd',
      primaryCurrencySymbol: ' US$ ',
      secondaryCurrencyCode: 'BsS',
      secondaryCurrencySymbol: ' Bs.S ',
      isConfigured: true,
    });

    assert.strictEqual(normalized.mode, 'Dual');
    assert.strictEqual(normalized.primaryCurrencyCode, 'USD');
    assert.strictEqual(normalized.primaryCurrencySymbol, 'US$');
    assert.strictEqual(normalized.secondaryCurrencyCode, 'VES');
    assert.strictEqual(normalized.secondaryCurrencySymbol, 'Bs.S');
    assert.strictEqual(displayCurrencyCode(normalized.secondaryCurrencyCode), 'Bs.S');
    assert.strictEqual(normalizeCurrencyCode('Bs.S'), 'VES');
    assert.strictEqual(normalizeCurrencyCode('bss'), 'VES');
    assert.strictEqual(normalizeCurrencyCode('ves'), 'VES');
    assert.strictEqual(normalizeCurrencyCode('usd'), 'USD');
    assert.strictEqual(normalizeCurrencyCode('nope'), null);
    assert.strictEqual(displayCurrencyCode('usd'), 'USD');
    assert.strictEqual(displayCurrencyCode('nope'), '');
  });

  it('5. malformed symbols and same-currency Dual pairs fall back safely', () => {
    const normalized = normalizeMonetaryProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'VES',
      primaryCurrencySymbol: 'Bs\nS',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: '123456789',
    });

    assert.strictEqual(normalized.primaryCurrencyCode, 'VES');
    assert.strictEqual(normalized.primaryCurrencySymbol, 'Bs.S');
    assert.strictEqual(normalized.secondaryCurrencyCode, 'USD');
    assert.strictEqual(normalized.secondaryCurrencySymbol, '$');
  });
});

describe('monetaryProfile — symbol lookup and formatting', () => {
  it('6. symbolForCode resolves the profile slots, VES aliases and the USD/VES defaults', () => {
    const profile = normalizeMonetaryProfile({
      mode: 'Dual',
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: 'US$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
    });

    assert.strictEqual(symbolForCode(profile, 'USD'), 'US$');
    assert.strictEqual(symbolForCode(profile, 'VES'), 'Bs.S');
    assert.strictEqual(symbolForCode(profile, 'Bs.S'), 'Bs.S');
    assert.strictEqual(symbolForCode(profile, 'BsS'), 'Bs.S');
    assert.strictEqual(symbolForCode(null, 'USD'), '$');
    assert.strictEqual(symbolForCode(null, 'VES'), 'Bs.S');
    assert.strictEqual(symbolForCode(profile, 'EUR'), 'EUR');
    assert.strictEqual(symbolForCode(profile, ''), '');
    assert.strictEqual(symbolForCode(profile, null), '');
  });

  it('7. formatWithSymbol delegates numeric formatting to formatAmount', () => {
    assert.strictEqual(formatWithSymbol('$', 1250.5, 'Venezuelan'), '$ 1.250,50');
    assert.strictEqual(formatWithSymbol('Bs.S', 1250.5, 'International', 2), 'Bs.S 1,250.50');
    assert.strictEqual(formatWithSymbol('$', 1.5, 'International', 3), '$ 1.500');
    assert.strictEqual(formatWithSymbol('', 1250.5, 'International'), '1,250.50');
    assert.strictEqual(formatWithSymbol(null, null, 'International'), '0.00');
    assert.strictEqual(formatWithSymbol('$', null, 'International'), '$ 0.00');
  });

  it('8. hostile values never throw', () => {
    const hostile = { self: null };
    hostile.self = hostile;

    assert.doesNotThrow(() => normalizeMonetaryProfile(hostile));
    assert.doesNotThrow(() => normalizeMonetaryProfile(Symbol('x')));
    assert.strictEqual(formatWithSymbol({}, {}, 'International', -1), '0.00');
  });
});
