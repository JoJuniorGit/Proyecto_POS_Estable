// T5 (Paso 13 / 8.188, S1/S2/S5/S7): panel de caja chica del turno (paridad WPF).
// Listado con categoría/concepto/beneficiario/montos duales/estado/autorizador/vale, saldos
// físicos visibles solo para supervisión (arqueo ciego) y acciones por estado.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import PettyCashPanel from './PettyCashPanel.jsx';

const expenses = [
  {
    id: 1,
    voucherNumber: 1,
    voucherCode: 'VCC-000001',
    sessionId: 42,
    category: 0,
    currency: 0,
    amountBsS: 1825,
    amountUsd: 50,
    historicalRate: 36.5,
    concept: 'Jabón y desinfectante',
    beneficiary: 'Limpieza Total C.A.',
    referenceNumber: 'FAC-001',
    createdByUserId: 7,
    createdAtUtc: '2026-10-18T14:30:00Z',
    authorizedByUserId: 9,
    status: 0,
  },
  {
    id: 2,
    voucherNumber: 2,
    voucherCode: 'VCC-000002',
    sessionId: 42,
    category: 2,
    currency: 1,
    amountBsS: 912.5,
    amountUsd: 25,
    historicalRate: 36.5,
    concept: 'Cambio de bomba de agua',
    beneficiary: 'Servicios Hidro',
    referenceNumber: 'REC-77',
    createdByUserId: 5,
    createdAtUtc: '2026-10-18T15:00:00Z',
    authorizedByUserId: 5,
    status: 1,
    cancellationReason: 'Duplicado',
  },
];

// React intercala marcadores de comentario entre nodos de texto en SSR.
function visibleText(html) {
  return html.replace(/<!--[\s\S]*?-->/g, '');
}

function render(overrides = {}) {
  return visibleText(renderToString(
    React.createElement(PettyCashPanel, {
      expenses,
      balances: { bsS: 1500.25, usd: 30 },
      canViewBalances: true,
      loading: false,
      onRegister: () => {},
      onCancelExpense: () => {},
      onRefresh: () => {},
      onClose: () => {},
      onDownloadVoucher: () => {},
      ...overrides,
    })
  ));
}

describe('PettyCashPanel [T5]', () => {
  it('1. renders the turn panel with dual amounts, status, authorizer and voucher code', () => {
    const html = render();

    assert.match(html, /CAJA CHICA DEL TURNO/);
    assert.match(html, /REGISTRAR GASTO/);
    assert.match(html, /VCC-000001/);
    assert.match(html, /VCC-000002/);
    assert.match(html, /Insumos y Limpieza/);
    assert.match(html, /Jabón y desinfectante/);
    assert.match(html, /Limpieza Total C\.A\./);
    assert.match(html, /FAC-001/);
    assert.match(html, /Activo/);
    assert.match(html, /Anulado/);
    assert.match(html, /Usuario #9/);
    assert.match(html, /Usuario #5/);
    assert.match(html, /1 activo\(s\)/);
  });

  it('2. hides the theoretical balances for a Cashier (arqueo ciego) and shows them for supervisors', () => {
    const cashierHtml = render({ canViewBalances: false, balances: null });

    assert.match(cashierHtml, /Efectivo físico Bs\.S/);
    assert.match(cashierHtml, /Efectivo físico USD/);
    assert.match(cashierHtml, /—/);
    assert.doesNotMatch(cashierHtml, /1\.500,25/);

    const supervisorHtml = render();
    assert.match(supervisorHtml, /1\.500,25/);
  });

  it('3. offers the voucher for every row and the cancel action only for active rows', () => {
    const html = render();
    const voucherButtons = html.match(/Descargar vale/g) || [];
    const cancelButtons = html.match(/Anular gasto/g) || [];

    assert.strictEqual(voucherButtons.length, 2);
    assert.strictEqual(cancelButtons.length, 1);
  });

  it('4. renders the empty state without expenses', () => {
    const html = render({ expenses: [] });

    assert.match(html, /No hay gastos de caja chica registrados en este turno\./);
  });
});
