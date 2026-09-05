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

    assert.strictEqual(retailBsS, 'Bs.S 100,00');
    assert.strictEqual(retailUSD, '$ 2,50');
    assert.strictEqual(wholesaleBsS, 'Bs.S 80,00');
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
    assert.strictEqual(retailUSD, '$ 3,00');
  });

  it('4. Formats numbers correctly in Venezuelan and International formats', async () => {
    const { formatAmount, formatBsS, formatUSD } = await import('./formatters.js');
    
    // Formato Venezolano (punto miles, coma decimal)
    assert.strictEqual(formatAmount(120, 'Venezuelan'), '120,00');
    assert.strictEqual(formatAmount(2450.5, 'Venezuelan'), '2.450,50');
    assert.strictEqual(formatAmount(172786.94, 'Venezuelan'), '172.786,94');
    assert.strictEqual(formatAmount(1000000, 'Venezuelan'), '1.000.000,00');
    assert.strictEqual(formatAmount(0, 'Venezuelan'), '0,00');
    assert.strictEqual(formatAmount(null, 'Venezuelan'), '0,00');
    assert.strictEqual(formatBsS(172786.94, 2, 'Venezuelan'), 'Bs.S 172.786,94');
    assert.strictEqual(formatUSD(1250.5, 2, 'Venezuelan'), '$ 1.250,50');

    // Formato Internacional (coma miles, punto decimal)
    assert.strictEqual(formatAmount(120, 'International'), '120.00');
    assert.strictEqual(formatAmount(2450.5, 'International'), '2,450.50');
    assert.strictEqual(formatAmount(172786.94, 'International'), '172,786.94');
    assert.strictEqual(formatAmount(1000000, 'International'), '1,000,000.00');
    assert.strictEqual(formatAmount(0, 'International'), '0.00');
    assert.strictEqual(formatAmount(null, 'International'), '0.00');
    assert.strictEqual(formatBsS(172786.94, 2, 'International'), 'Bs.S 172,786.94');
    assert.strictEqual(formatUSD(1250.5, 2, 'International'), '$ 1,250.50');
  });

  it('5. Parses amounts flexibly according to system parsing rules', async () => {
    const { parseAmount } = await import('./formatters.js');

    // Sin separadores -> entero independiente del formato
    assert.strictEqual(parseAmount('72915'), 72915);
    assert.strictEqual(parseAmount(72915), 72915);

    // Formato venezolano con separadores de miles y decimal
    assert.strictEqual(parseAmount('172.786,94'), 172786.94);
    assert.strictEqual(parseAmount('1.234.567,89'), 1234567.89);
    assert.strictEqual(parseAmount('Bs.S 172.786,94'), 172786.94);

    // Formato internacional con separadores de miles y decimal
    assert.strictEqual(parseAmount('172,786.94'), 172786.94);
    assert.strictEqual(parseAmount('1,234,567.89'), 1234567.89);
    assert.strictEqual(parseAmount('$ 172,786.94'), 172786.94);

    // Un solo separador (asumido decimal)
    assert.strictEqual(parseAmount('172,94'), 172.94);
    assert.strictEqual(parseAmount('172.94'), 172.94);
    assert.strictEqual(parseAmount('72915,00'), 72915);
    assert.strictEqual(parseAmount('72915.00'), 72915);
    assert.strictEqual(parseAmount(',50'), 0.5);
    assert.strictEqual(parseAmount('.50'), 0.5);

    // Entradas vacías o inválidas -> 0
    assert.strictEqual(parseAmount(''), 0);
    assert.strictEqual(parseAmount(null), 0);
    assert.strictEqual(parseAmount(undefined), 0);
    assert.strictEqual(parseAmount('   '), 0);
    assert.strictEqual(parseAmount('abc'), 0);
    assert.strictEqual(parseAmount('12a3'), 0);
    assert.strictEqual(parseAmount('172,,94'), 0); // Dos comas consecutivas
    assert.strictEqual(parseAmount('172..94'), 0); // Dos puntos consecutivos
  });
});
