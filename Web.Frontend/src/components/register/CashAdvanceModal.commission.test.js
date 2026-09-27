import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CashAdvanceModal, {
  resolveCommissionPercentage,
  computeAdvanceSummary,
} from './CashAdvanceModal.jsx';

/**
 * REQ-CAP-02/03 — the advance preview reads the server-resolved percentage
 * (no 7/10 literal) and fails closed when the read cannot resolve it.
 */
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const modalPath = path.resolve(__dirname, 'CashAdvanceModal.jsx');
const source = fs.readFileSync(modalPath, 'utf-8');

describe('CashAdvanceModal — server-resolved commission (REQ-CAP-02/03)', () => {
  it('takes the percentage from the server response, not from a literal', () => {
    assert.equal(resolveCommissionPercentage({ isTransfer: true, percentage: 5.5 }), 5.5);
    assert.equal(resolveCommissionPercentage({ percentage: 12.25 }), 12.25);
    assert.equal(resolveCommissionPercentage(null), null);
    assert.equal(resolveCommissionPercentage(undefined), null);
    assert.equal(resolveCommissionPercentage({}), null);
    assert.equal(resolveCommissionPercentage({ percentage: 0 }), null);
    assert.equal(resolveCommissionPercentage({ percentage: -1 }), null);
    assert.equal(resolveCommissionPercentage({ percentage: '7' }), null);
  });

  it('computes the preview from the resolved percentage only', () => {
    const resolved = computeAdvanceSummary({ amountBsS: 1000, percentage: 5.5, exchangeRate: 50 });

    assert.equal(resolved.hasResolvedCommission, true);
    assert.equal(resolved.commissionBsS, 55);
    assert.equal(resolved.totalChargedBsS, 1055);
    assert.equal(resolved.totalChargedUsd, 21.1);

    const unresolved = computeAdvanceSummary({ amountBsS: 1000, percentage: null, exchangeRate: 50 });

    assert.equal(unresolved.hasResolvedCommission, false);
    assert.equal(unresolved.commissionBsS, null);
    assert.equal(unresolved.totalChargedBsS, null);
    assert.equal(unresolved.totalChargedUsd, null);
  });

  it('reads the new server route and blocks submission while unresolved', () => {
    assert.ok(
      /\/api\/cashdrawer\/advance-commission\?isTransfer=\$\{isTransfer\}/.test(source),
      'the preview must read GET /api/cashdrawer/advance-commission'
    );
    assert.ok(
      /disabled=\{loading \|\| numRequested <= 0 \|\| !hasResolvedCommission\}/.test(source),
      'submit must stay disabled without a resolved percentage'
    );
  });

  it('keeps no hardcoded 7/10 commission literal', () => {
    const forbidden = [
      /\?\s*7\s*:\s*10/,
      /7\s*:\s*10/,
      /Comisi[oó]n\s*7/,
      /Comisi[oó]n\s*10/,
      /percentage\s*=\s*(7|10)\b/,
    ];

    for (const pattern of forbidden) {
      assert.ok(!pattern.test(source), `Hardcoded commission literal found: ${pattern}`);
    }
  });

  it('renders without a fabricated percentage and with submission blocked', () => {
    const html = renderToString(
      React.createElement(CashAdvanceModal, {
        isOpen: true,
        onClose: () => {},
        sessionId: 1,
        availableCashBsS: 2000,
        exchangeRate: 50,
        user: { id: 1, name: 'Cajero' },
      })
    );

    assert.ok(html.includes('No se pudo obtener la comisión configurada del servidor'));
    assert.ok(!html.includes('7%'));
    assert.ok(!html.includes('10%'));

    const submitButton = html.match(/<button[^>]*type="submit"[^>]*>/);
    assert.ok(submitButton, 'the submit button must render');
    assert.ok(submitButton[0].includes('disabled'), 'the submit button must be disabled while unresolved');
  });
});
