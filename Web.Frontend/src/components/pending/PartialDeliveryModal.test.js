import { describe, test } from 'node:test';
import assert from 'node:assert';
import React from 'react';
import { renderToString } from 'react-dom/server';
import PartialDeliveryModal from './PartialDeliveryModal.jsx';
import { deliverPartialPickup, getDeliveryNoteBlob } from '../../services/pendingPickupApi.js';

async function withFetch(fetchImplementation, action) {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = fetchImplementation;
  try {
    return await action();
  } finally {
    globalThis.fetch = originalFetch;
  }
}

describe('PartialDeliveryModal and delivery API', () => {
  test('server render includes accessible quantity controls and disables an empty dispatch', () => {
    const html = renderToString(React.createElement(PartialDeliveryModal, {
      pickup: {
        saleId: 42,
        invoiceNumber: 123,
        customerName: 'Cliente de prueba',
        items: [{ saleItemId: 7, productName: 'Producto fraccionable', totalQuantity: 4, deliveredQuantity: 1, pendingQuantity: 3 }],
      },
      onClose: () => {},
      onConfirm: async () => {},
    }));

    assert.match(html, /role="dialog"/);
    assert.match(html, /aria-modal="true"/);
    assert.match(html, /Factura N°/);
    assert.match(html, /123/);
    assert.match(html, /Producto fraccionable/);
    assert.match(html, /Pendiente: <!-- -->3/);
    assert.match(html, /step="any"/);
    assert.match(html, /Cantidad a retirar hoy/);
    assert.match(html, /Notas \(opcional\)/);

    const submitButton = html.match(/<button\b[^>]*>/g)?.find((button) => button.includes('type="submit"'));
    assert.ok(submitButton?.includes('disabled'), 'dispatch confirmation starts disabled');
  });

  test('deliverPartialPickup sends the full line payload and idempotency header', async () => {
    let request;
    const receipt = { deliveryId: 19, saleId: 42, deliveryStatus: 'PartiallyDelivered' };
    const result = await withFetch(async (url, options) => {
      request = { url, options };
      return new Response(JSON.stringify(receipt), {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      });
    }, () => deliverPartialPickup(
      42,
      [{ saleItemId: 7, quantity: 1.25 }],
      'Retira un familiar',
      'delivery-attempt-42'
    ));

    assert.deepEqual(result, receipt);
    assert.equal(request.url, 'http://localhost:5000/api/sales/42/deliveries');
    assert.equal(request.options.method, 'POST');
    assert.equal(request.options.credentials, 'include');
    assert.equal(request.options.headers['Idempotency-Key'], 'delivery-attempt-42');
    assert.deepEqual(JSON.parse(request.options.body), {
      items: [{ saleItemId: 7, quantity: 1.25 }],
      notes: 'Retira un familiar',
    });
  });

  test('getDeliveryNoteBlob fetches the sale-scoped PDF with the authenticated client', async () => {
    let request;
    const pdfBytes = new Uint8Array([37, 80, 68, 70]);
    const blob = await withFetch(async (url, options) => {
      request = { url, options };
      return new Response(pdfBytes, {
        status: 200,
        headers: { 'Content-Type': 'application/pdf' },
      });
    }, () => getDeliveryNoteBlob(42, 19));

    assert.ok(blob instanceof Blob);
    assert.equal(await blob.text(), '%PDF');
    assert.equal(request.url, 'http://localhost:5000/api/sales/42/deliveries/19/receipt');
    assert.equal(request.options.method, 'GET');
    assert.equal(request.options.credentials, 'include');
    assert.equal(request.options.headers.Accept, 'application/pdf');
  });
});
