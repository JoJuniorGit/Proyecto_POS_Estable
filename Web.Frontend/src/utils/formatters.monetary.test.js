import assert from 'node:assert/strict';
import test from 'node:test';
import { formatBsS, formatUSD } from './formatters.js';

const dualUsdCop = {
  mode: 'Dual',
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'COP',
  secondaryCurrencySymbol: 'COP',
};

const singleCop = {
  mode: 'Single',
  primaryCurrencyCode: 'COP',
  primaryCurrencySymbol: 'COP',
  secondaryCurrencyCode: null,
  secondaryCurrencySymbol: null,
};

test('formatBsS uses the profile secondary symbol in Dual', () => {
  assert.equal(formatBsS(10, 2, 'Venezuelan', dualUsdCop), 'COP 10,00');
});

test('formatUSD uses the profile primary symbol in Dual', () => {
  assert.equal(formatUSD(10, 2, 'International', dualUsdCop), '$ 10.00');
});

test('formatBsS and formatUSD collapse to the primary in Single', () => {
  assert.equal(formatBsS(10, 2, 'Venezuelan', singleCop), 'COP 10,00');
  assert.equal(formatUSD(10, 2, 'Venezuelan', singleCop), 'COP 10,00');
});

test('without a profile the legacy Bs.S/$ labels remain', () => {
  assert.equal(formatBsS(10, 2, 'Venezuelan', null), 'Bs.S 10,00');
  assert.equal(formatUSD(10, 2, 'Venezuelan', null), '$ 10,00');
});

test('VES presentation keeps Bs.S even with a profile', () => {
  const dualUsdVes = {
    mode: 'Dual',
    primaryCurrencyCode: 'USD',
    primaryCurrencySymbol: '$',
    secondaryCurrencyCode: 'VES',
    secondaryCurrencySymbol: 'Bs.S',
  };
  assert.equal(formatBsS(10, 2, 'Venezuelan', dualUsdVes), 'Bs.S 10,00');
});
