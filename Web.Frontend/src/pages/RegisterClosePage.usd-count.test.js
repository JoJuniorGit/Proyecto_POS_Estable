import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Residual 8.187-8.188: el cierre ciego web ofrece el conteo fisico USD opcional y muestra el
 * esperado/diferencia USD solo a roles supervisores (el servidor aplica blind 8.153).
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pagePath = path.resolve(__dirname, '../../src/pages/RegisterClosePage.jsx');
const source = fs.readFileSync(pagePath, 'utf-8');

describe('RegisterClosePage - conteo USD opcional', () => {
  it('renders the optional USD count field', () => {
    assert.match(source, /Efectivo USD contado/);
    assert.match(source, /actualClosingBalanceUsd/);
  });

  it('sends the optional USD count through closeShift', () => {
    assert.match(source, /closeShift\(\s*payloadAmounts\s*,\s*actualClosingBalanceUsd\s*\)/);
  });

  it('gates expected/difference USD through the supervisor role helper', () => {
    assert.match(source, /canViewUsdReconciliation/);
    assert.match(source, /expectedClosingBalanceUsd/);
    assert.match(source, /differenceUsd/);
  });
});
