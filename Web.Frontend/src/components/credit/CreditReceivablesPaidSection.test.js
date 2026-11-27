import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import CreditReceivablesPaidSection from './CreditReceivablesPaidSection.jsx';

globalThis.sessionStorage = {
  getItem: () => null,
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

globalThis.localStorage = {
  getItem: () => null,
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

describe('CreditReceivablesPaidSection (Deudas Pagadas)', () => {
  it('renders the paid filters, realized KPIs and invoice/payment modes', () => {
    const html = renderToString(<CreditReceivablesPaidSection />);

    assert.match(html, /Cliente, cédula\/RIF, N° factura o referencia/);
    assert.match(html, /DEUDAS PAGADAS/);
    assert.match(html, /ABONOS REGISTRADOS/);
    assert.match(html, /TIEMPO PROM\. DE PAGO/);
    assert.match(html, /Por factura/);
    assert.match(html, /Por abono/);
    assert.match(html, /Hoy/);
    assert.match(html, /Mes Actual/);
    assert.match(html, /CSV/);
    assert.match(html, /Excel/);
    assert.match(html, /PDF/);
  });

  it('does not expose the pending-only status filter in the paid section', () => {
    const html = renderToString(<CreditReceivablesPaidSection />);
    assert.doesNotMatch(html, /Solo en mora/);
    assert.doesNotMatch(html, /Pagada<\/option>/);
  });
});
