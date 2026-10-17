import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveScannedPreviewPrice } from './BarcodeScannerModal.jsx';

/**
 * 8.186-T2 (S1/S3, web): el preview del escaneo (quick-check) muestra el Bs.S EFECTIVO
 * (`effectivePriceBsS`) proyectado por el servidor; el canónico USD × tasa (8.143) queda solo
 * como fallback documentado cuando el campo falta o viene <= 0.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.resolve(__dirname, 'BarcodeScannerModal.jsx'), 'utf-8');
const normalizedSource = source.replace(/\s+/g, ' ');

describe('BarcodeScannerModal — preview con el efectivo del servidor (8.186-T2, S1/S3)', () => {
  it('1. quick-check con effectivePriceBsS muestra el efectivo, no el canónico', () => {
    const price = resolveScannedPreviewPrice({ priceUSD: 10, priceBsS: 100, effectivePriceBsS: 874.5 }, 50);
    assert.strictEqual(price, 'Bs.S 874,50');
  });

  it('2. Fallback canónico documentado cuando el campo efectivo falta', () => {
    const price = resolveScannedPreviewPrice({ priceUSD: 10, priceBsS: 100 }, 50);
    assert.strictEqual(price, 'Bs.S 500,00');
  });

  it('3. Fallback canónico documentado cuando el campo efectivo viene 0', () => {
    const price = resolveScannedPreviewPrice({ priceUSD: 10, priceBsS: 100, effectivePriceBsS: 0 }, 50);
    assert.strictEqual(price, 'Bs.S 500,00');
  });

  it('4. Sin Bs.S convertible (efectivo 0, USD 0) conserva el comportamiento 8.143: muestra USD', () => {
    const price = resolveScannedPreviewPrice({ priceUSD: 0, priceBsS: 0, effectivePriceBsS: 0 }, 50);
    assert.strictEqual(price, '$ 0,00');
  });

  it('5. La fuente prioriza el campo del servidor y no implementa política de redondeo (S3)', () => {
    assert.ok(
      normalizedSource.includes('resolveScannedPreviewPrice(info, exchangeRateRef.current)'),
      'handleDecodedCode debe usar el resolvedor de preview'
    );
    assert.ok(!source.includes('Math.ceil'), 'sin matemática de política en JS (S3)');
  });
});
