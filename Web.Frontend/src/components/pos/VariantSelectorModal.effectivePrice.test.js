import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveVariantDisplayBsS } from './VariantSelectorModal.jsx';

/**
 * 8.186-T2 (S1/S3, web): las tarjetas del selector de variantes muestran el Bs.S EFECTIVO
 * (`effectivePriceBsS`) proyectado por el servidor; el canónico (retail USD de la variante o del
 * padre × tasa, 8.143) queda solo como fallback documentado cuando el campo falta o viene <= 0.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'VariantSelectorModal.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('VariantSelectorModal — variantes con el efectivo del servidor (8.186-T2, S1/S3)', () => {
  it('1. effectivePriceBsS de la variante gana sobre el canónico USD × tasa', () => {
    const displayBsS = resolveVariantDisplayBsS(
      { id: 2, priceRetailUSD: 10, priceBsS: 100, effectivePriceBsS: 874.5 },
      12,
      50
    );
    assert.strictEqual(displayBsS, 874.5);
  });

  it('2. Fallback canónico con el retail USD propio de la variante cuando el efectivo falta', () => {
    const displayBsS = resolveVariantDisplayBsS({ id: 3, priceRetailUSD: 10, priceBsS: 100 }, 12, 50);
    assert.strictEqual(displayBsS, 500);
  });

  it('3. Fallback canónico con el precio base del padre cuando la variante no trae retail USD', () => {
    const displayBsS = resolveVariantDisplayBsS({ id: 4, priceBsS: 100 }, 12, 50);
    assert.strictEqual(displayBsS, 600);
  });

  it('4. Fallback al snapshot persistido sin USD ni tasa válidos', () => {
    const displayBsS = resolveVariantDisplayBsS({ id: 5, priceBsS: 300 }, 0, 0);
    assert.strictEqual(displayBsS, 300);
  });

  it('5. Sin efectivo ni canónico devuelve 0 (la tarjeta omite el precio)', () => {
    const displayBsS = resolveVariantDisplayBsS({ id: 6, priceBsS: 0 }, 0, 0);
    assert.strictEqual(displayBsS, 0);
  });

  it('6. La fuente usa el resolvedor y no implementa política de redondeo (S3)', () => {
    assert.ok(
      normalizedSource.includes('resolveVariantDisplayBsS(v, basePriceUSD, exchangeRate)'),
      'la tarjeta debe consumir el resolvedor de variante'
    );
    assert.ok(!source.includes('Math.ceil'), 'sin matemática de política en JS (S3)');
  });
});
