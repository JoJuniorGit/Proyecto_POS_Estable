import { describe, it } from 'node:test';
import assert from 'node:assert';
import { formatProductDisplayPrice } from './formatters.js';

describe('formatters.js formatProductDisplayPrice', () => {
  it('1. Returns "—" for group header with independent pricing', () => {
    const parent = {
      id: 1,
      name: 'Refresco 2L Sabores',
      isGroupHeader: true,
      hasIndependentPricing: true,
      priceUSD: 0,
      priceBsS: 0
    };

    const retailPrice = formatProductDisplayPrice(parent, false, 'Bs.S', 36.5);
    const wholesalePrice = formatProductDisplayPrice(parent, true, 'Bs.S', 36.5);

    assert.strictEqual(retailPrice, '—');
    assert.strictEqual(wholesalePrice, '—');
  });

  it('2. Formats prices normally for group header with inherited/shared pricing', () => {
    const parent = {
      id: 1,
      name: 'Refresco 2L Sabores',
      isGroupHeader: true,
      hasIndependentPricing: false,
      priceUSD: 2.5,
      hasWholesale: true,
      priceWholesaleUSD: 2.0
    };

    const retailBsS = formatProductDisplayPrice(parent, false, 'Bs.S', 40.0);
    const retailUSD = formatProductDisplayPrice(parent, false, 'USD', 40.0);
    const wholesaleBsS = formatProductDisplayPrice(parent, true, 'Bs.S', 40.0);

    assert.strictEqual(retailBsS, 'Bs.S 100.00');
    assert.strictEqual(retailUSD, '$ 2.50');
    assert.strictEqual(wholesaleBsS, 'Bs.S 80.00');
  });

  it('3. Formats prices normally for regular products and variants', () => {
    const variant = {
      id: 2,
      name: 'Refresco 2L Fresa',
      parentProductId: 1,
      isGroupHeader: false,
      hasIndependentPricing: false,
      priceUSD: 3.0
    };

    const retailUSD = formatProductDisplayPrice(variant, false, 'USD', 40.0);
    assert.strictEqual(retailUSD, '$ 3.00');
  });

  it('4. Formats numbers with commas and decimals correctly using formatNumberEs', async () => {
    const { formatNumberEs } = await import('./formatters.js');
    assert.strictEqual(formatNumberEs(120), '120.00');
    assert.strictEqual(formatNumberEs(2450.5), '2,450.50');
    assert.strictEqual(formatNumberEs(1000000), '1,000,000.00');
    assert.strictEqual(formatNumberEs(0), '0.00');
    assert.strictEqual(formatNumberEs(null), '0.00');
    assert.strictEqual(formatNumberEs(undefined), '0.00');
    assert.strictEqual(formatNumberEs('2450.5'), '2,450.50');
  });
});
