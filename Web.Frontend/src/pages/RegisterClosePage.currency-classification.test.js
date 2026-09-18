import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * S1 — REQ-PMC-05: RegisterClosePage MUST NOT classify currency from method names
 * or substrings. Any currency label MUST come from the server classification.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pagePath = path.resolve(__dirname, '../../src/pages/RegisterClosePage.jsx');
const source = fs.readFileSync(pagePath, 'utf-8');

describe('RegisterClosePage — REQ-PMC-05 no local heuristic', () => {
  it('does not use name/substring heuristic for currency classification', () => {
    // REQ-PMC-05 Scenario: No local heuristic remains
    // The getMethodCurrency helper must only read method.currency from the server
    const heuristicPatterns = [
      /\.includes\(['"]USD['"]\)/i,
      /\.includes\(['"]dolar/i,
      /\.includes\(['"]\$\)/,
      /\.includes\(['"]divisa/i,
      /usd.*\|\|.*dolar/i,
      /dolar.*\|\|.*usd/i,
      /\$.*\|\|.*divisa/i,
    ];

    for (const pattern of heuristicPatterns) {
      assert.ok(!pattern.test(source), `Pattern ${pattern} must not appear in RegisterClosePage.jsx`);
    }
  });

  it('getMethodCurrency reads only from server-provided method.currency', () => {
    // The helper must be: method?.currency === 'USD' ? 'USD' : 'Bs.S'
    // No fallback to name-based detection
    const helperMatch = source.match(
      /getMethodCurrency\s*=?\s*(?:useCallback\s*\(\s*)?\(?(\w+)\)?\s*=>\s*\{[\s\S]*?\}/
    );
    assert.ok(helperMatch, 'getMethodCurrency helper must exist');

    const helperBody = helperMatch[0];
    // Must reference method.currency (server field)
    assert.ok(/method\?\.currency/.test(helperBody), 'getMethodCurrency must read method.currency');
    // Must NOT contain name-based fallback
    assert.ok(!/\.name/i.test(helperBody), 'getMethodCurrency must not use method.name');
    assert.ok(!/\.includes\(/.test(helperBody), 'getMethodCurrency must not use .includes()');
  });

  it('payload omits the currency key from declaredAmounts', () => {
    // REQ-PMC-04/05: payload MUST NOT include a currency field
    const payloadSection = source.match(
      /const payloadAmounts[\s\S]*?(?=\n\s*try)/
    );
    assert.ok(payloadSection, 'payloadAmounts variable must exist');

    const payload = payloadSection[0];
    // Must NOT have currency in the mapped object
    assert.ok(!/currency\s*:/.test(payload), 'payload must not contain currency key');
    // Must have paymentMethodId, paymentMethodName, amount
    assert.ok(/paymentMethodId/.test(payload), 'payload must have paymentMethodId');
    assert.ok(/paymentMethodName/.test(payload), 'payload must have paymentMethodName');
    assert.ok(/amount/.test(payload), 'payload must have amount');
  });
});
