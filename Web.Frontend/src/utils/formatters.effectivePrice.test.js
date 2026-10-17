import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  formatProductDisplayPrice,
  resolveBsSPrice,
  resolveEffectiveBsS,
  shouldRefetchOnRateChange,
} from './formatters.js';

/**
 * 8.186-T2 (S1/S3, web): el precio Bs.S mostrado en catálogo/buscador web es el EFECTIVO
 * proyectado server-side (`effectivePriceBsS` / `effectiveWholesalePriceBsS`). Web no replica la
 * política de redondeo: solo consume y formatea; el canónico local (`resolveBsSPrice`, 8.143)
 * queda como fallback documentado de legado cuando el campo falta o viene <= 0.
 */
describe('formatters — precio efectivo Bs.S consumido del servidor (8.186-T2, S1/S3)', () => {
  it('1. Prioriza el campo efectivo del servidor sobre el canónico local', () => {
    assert.strictEqual(resolveEffectiveBsS(438, 500), 438);
  });

  it('2. Campo efectivo ausente/0/negativo → fallback canónico documentado', () => {
    assert.strictEqual(resolveEffectiveBsS(undefined, 500), 500);
    assert.strictEqual(resolveEffectiveBsS(null, 500), 500);
    assert.strictEqual(resolveEffectiveBsS(0, 500), 500);
    assert.strictEqual(resolveEffectiveBsS(-1, 500), 500);
  });

  it('3. Sin efectivo ni canónico → 0 (no inventa precios)', () => {
    assert.strictEqual(resolveEffectiveBsS(0, 0), 0);
    assert.strictEqual(resolveEffectiveBsS(undefined, undefined), 0);
    assert.strictEqual(resolveEffectiveBsS(NaN, NaN), 0);
  });

  it('4. formatProductDisplayPrice muestra Detal y Mayor efectivos del servidor', () => {
    const product = {
      priceUSD: 10,
      priceBsS: 100,
      hasWholesale: true,
      priceWholesaleUSD: 9,
      effectivePriceBsS: 874.5,
      effectiveWholesalePriceBsS: 790,
    };
    assert.strictEqual(formatProductDisplayPrice(product, false, 'Bs.S', 50), 'Bs.S 874,50');
    assert.strictEqual(formatProductDisplayPrice(product, true, 'Bs.S', 50), 'Bs.S 790,00');
  });

  it('5. formatProductDisplayPrice conserva el canónico 8.143 como fallback sin campo efectivo', () => {
    const product = { priceUSD: 10, priceBsS: 100 };
    assert.strictEqual(formatProductDisplayPrice(product, false, 'Bs.S', 50), 'Bs.S 500,00');
    assert.strictEqual(formatProductDisplayPrice(product, true, 'Bs.S', 50), 'Bs.S 500,00');
  });

  it('6. El fallback canónico sigue siendo USD × tasa con techo (8.143) para respuestas legadas', () => {
    assert.strictEqual(resolveBsSPrice(3.333, 0, 50), 166.65);
    assert.strictEqual(resolveBsSPrice(0, 300, 50), 300);
  });
});

describe('formatters — reactividad de tasa (8.186-T2, S1)', () => {
  it('1. Refetch sólo con un cambio real entre dos tasas positivas', () => {
    assert.strictEqual(shouldRefetchOnRateChange(36.5, 40), true);
    assert.strictEqual(shouldRefetchOnRateChange(40, 36.5), true);
  });

  it('2. Ignora la transición inicial 0 → tasa (sin doble fetch de montaje)', () => {
    assert.strictEqual(shouldRefetchOnRateChange(0, 36.5), false);
    assert.strictEqual(shouldRefetchOnRateChange(undefined, 36.5), false);
    assert.strictEqual(shouldRefetchOnRateChange(null, 36.5), false);
  });

  it('3. Ignora repeticiones y caídas a 0 (sin loops de refetch)', () => {
    assert.strictEqual(shouldRefetchOnRateChange(36.5, 36.5), false);
    assert.strictEqual(shouldRefetchOnRateChange(36.5, 0), false);
    assert.strictEqual(shouldRefetchOnRateChange(0, 0), false);
  });
});
