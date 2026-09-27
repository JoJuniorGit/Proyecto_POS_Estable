import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * REQ-ADB-05 — the movements filter and labels resolve from the contract-aligned
 * mapping in constants/cashTransactionSource.js; no bare source ordinal remains.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pagePath = path.resolve(__dirname, '../../src/pages/RegisterPage.jsx');
const source = fs.readFileSync(pagePath, 'utf-8');

describe('RegisterPage — REQ-ADB-05 source filter mapping', () => {
  it('imports the filter and labels from the single mapping module', () => {
    assert.ok(
      /from '\.\.\/constants\/cashTransactionSource'/.test(source),
      'RegisterPage must import the source mapping module'
    );
    assert.ok(
      /matchesSourceFilter\(sourceFilter,\s*tx\.source\)/.test(source),
      'the source filter must resolve through matchesSourceFilter'
    );
    assert.ok(
      /getSourceLabel\(tx\.source\)/.test(source),
      'labels must come from the shared getSourceLabel'
    );
  });

  it('does not compare tx.source against a bare ordinal literal', () => {
    const bareSourceComparisons = [
      /source\s*!==\s*\d+/,
      /source\s*===\s*\d+/,
      /source\s*!=\s*\d+/,
      /source\s*==\s*\d+/,
    ];

    for (const pattern of bareSourceComparisons) {
      assert.ok(!pattern.test(source), `Bare source ordinal found: ${pattern}`);
    }
  });

  it('resolves the opening exclusion from the contract enum', () => {
    assert.ok(
      /\bsource\s*!==\s*CashTransactionSource\.Opening/.test(source),
      'the opening exclusion must reference CashTransactionSource.Opening'
    );
    assert.ok(
      !/function\s+getSourceLabel\s*\(/.test(source),
      'the local label switch must be deleted'
    );
  });
});
