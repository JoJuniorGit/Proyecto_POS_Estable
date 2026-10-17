import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveCatalogProductPrices } from './CatalogPage.jsx';

/**
 * 8.186-T2 (S1/S3, web): la grilla del catálogo (escritorio y móvil) muestra el Bs.S EFECTIVO
 * proyectado server-side (`effectivePriceBsS` Detal / `effectiveWholesalePriceBsS` Mayor) y NO
 * recalcula la política de redondeo en JS; el canónico 8.143 queda solo como fallback legado.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'CatalogPage.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('CatalogPage — precio efectivo Bs.S del servidor (8.186-T2, S1/S3)', () => {
  it('1. Detal usa effectivePriceBsS aunque el canónico USD × tasa difiera', () => {
    const product = { priceUSD: 5, priceBsS: 400, effectivePriceBsS: 438 };
    const row = resolveCatalogProductPrices(product, 100);
    assert.strictEqual(row.retailBsS, 438);
    assert.notStrictEqual(row.retailBsS, 500, 'no debe usar el canónico USD × tasa');
  });

  it('2. Mayor real usa effectiveWholesalePriceBsS del servidor', () => {
    const product = {
      priceUSD: 10,
      priceBsS: 800,
      hasWholesale: true,
      priceWholesaleUSD: 9,
      effectivePriceBsS: 873.5,
      effectiveWholesalePriceBsS: 790,
    };
    const row = resolveCatalogProductPrices(product, 100);
    assert.strictEqual(row.hasRealWholesale, true);
    assert.strictEqual(row.wholesaleBsS, 790);
  });

  it('3. Sin mayor real el canal Mayor hereda el Detal EFECTIVO (regla vigente)', () => {
    const product = { priceUSD: 10, priceBsS: 800, hasWholesale: false, priceWholesaleUSD: 0, effectivePriceBsS: 873.5 };
    const row = resolveCatalogProductPrices(product, 100);
    assert.strictEqual(row.hasRealWholesale, false);
    assert.strictEqual(row.wholesaleBsS, 873.5);
    assert.strictEqual(row.wholesaleUSD, row.retailUSD);
  });

  it('4. Fallback canónico documentado cuando el campo efectivo Detal falta o viene 0', () => {
    const sinCampo = { priceUSD: 10, priceBsS: 100 };
    assert.strictEqual(resolveCatalogProductPrices(sinCampo, 50).retailBsS, 500);

    const campoCero = { priceUSD: 10, priceBsS: 100, effectivePriceBsS: 0 };
    assert.strictEqual(resolveCatalogProductPrices(campoCero, 50).retailBsS, 500);
  });

  it('5. Fallback Mayor canónico (USD mayor × tasa con techo) cuando el efectivo Mayor no viene', () => {
    const product = { priceUSD: 10, priceBsS: 800, hasWholesale: true, priceWholesaleUSD: 9, effectivePriceBsS: 873.5 };
    const row = resolveCatalogProductPrices(product, 100);
    assert.strictEqual(row.wholesaleBsS, 900);
  });

  it('6. Sin USD ni tasa válidos el fallback mantiene el snapshot persistido', () => {
    const product = { priceUSD: 0, priceBsS: 300 };
    assert.strictEqual(resolveCatalogProductPrices(product, 0).retailBsS, 300);
  });

  it('7. Escritorio y móvil comparten el resolvedor y no queda matemática de política en JS (S3)', () => {
    const usages = normalizedSource.match(/resolveCatalogProductPrices\(p, exchangeRate\)/g) || [];
    assert.strictEqual(usages.length, 2, 'ambos paths (escritorio y móvil) deben consumir el helper');
    assert.ok(!source.includes('usdToBs'), 'no debe quedar la conversión canónica local (S3)');
    assert.ok(!source.includes('toBsSCeiling'), 'no debe quedar el alias canónico local');

    // El resolvedor de precios no implementa la política de redondeo (pasos/techo); esa matemática
    // solo puede vivir en el fallback canónico ya existente de formatters.js (8.143).
    const helperBody = source.slice(
      source.indexOf('export function resolveCatalogProductPrices'),
      source.indexOf('export default function CatalogPage')
    );
    assert.ok(!helperBody.includes('Math.ceil'), 'el resolvedor de precios no implementa la política (S3)');
    assert.ok(!helperBody.includes('Math.round'), 'el resolvedor de precios no implementa la política (S3)');
  });

  it('8. El cambio de tasa dispara refetch del catálogo sin doble fetch inicial ni loops', () => {
    assert.ok(
      normalizedSource.includes('shouldRefetchOnRateChange(previousRate, exchangeRate)'),
      'el guard debe decidir el refetch por cambio de tasa'
    );
    assert.ok(
      normalizedSource.includes('setReloadToken((t) => t + 1);'),
      'el cambio de tasa debe incrementar el token que relanza el fetch'
    );
    assert.ok(
      normalizedSource.includes('}, [debouncedSearch, sortBy, sortDescending, currentPage, reloadToken, categoryId]);'),
      'exchangeRate no debe ir directo en deps del fetch (evita el doble fetch de montaje)'
    );
  });
});
