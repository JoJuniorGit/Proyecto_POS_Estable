import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { addPaymentsBatchToHoldSale, addPaymentToHoldSale } from './salesApi.js';

describe('salesApi addPaymentsBatchToHoldSale (atomic batch)', () => {
  let originalFetch;

  beforeEach(() => {
    originalFetch = global.fetch;
    global.fetch = async (url, config) => {
      global.__lastFetch = { url, config };
      return {
        ok: true,
        status: 200,
        statusText: 'OK',
        headers: { get: () => 'application/json' },
        text: async () => JSON.stringify({ id: 5 }),
        json: async () => ({ id: 5 }),
      };
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
    delete global.__lastFetch;
  });

  it('1. Envia el lote completo a /payments/batch con UN solo Idempotency-Key', async () => {
    const payments = [
      { paymentMethodId: 1, amountBsS: 300, exchangeRate: 50, referenceNumber: null },
      { paymentMethodId: 2, amountBsS: 200, exchangeRate: 50, referenceNumber: 'REF-X' },
    ];

    const result = await addPaymentsBatchToHoldSale(5, payments, 'batch-key-unica');

    assert.ok(global.__lastFetch, 'fetch deberia llamarse una sola vez');
    assert.ok(global.__lastFetch.url.includes('/api/sales/5/payments/batch'));
    assert.strictEqual(global.__lastFetch.config.method, 'POST');
    assert.strictEqual(global.__lastFetch.config.headers['Idempotency-Key'], 'batch-key-unica');
    const body = JSON.parse(global.__lastFetch.config.body);
    assert.strictEqual(body.length, 2);
    assert.deepStrictEqual(body, payments);
    assert.strictEqual(result.id, 5);
  });

  it('2. Genera una clave nueva cuando no se provee idempotencyKey', async () => {
    await addPaymentsBatchToHoldSale(9, [{ paymentMethodId: 1, amountBsS: 100, exchangeRate: 50 }]);

    const key = global.__lastFetch.config.headers['Idempotency-Key'];
    assert.ok(key && key.length > 0, 'debe existir un Idempotency-Key');
  });
});

describe('salesApi addPaymentToHoldSale (single, legacy contract intacto)', () => {
  let originalFetch;

  beforeEach(() => {
    originalFetch = global.fetch;
    global.fetch = async (url, config) => {
      global.__lastFetch = { url, config };
      return {
        ok: true,
        status: 200,
        statusText: 'OK',
        headers: { get: () => 'application/json' },
        text: async () => JSON.stringify({ id: 7 }),
        json: async () => ({ id: 7 }),
      };
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
    delete global.__lastFetch;
  });

  it('3. Sigue usando el endpoint singular /payments', async () => {
    await addPaymentToHoldSale(7, { paymentMethodId: 1, amountBsS: 150, exchangeRate: 50 }, 'key-singular');
    assert.ok(global.__lastFetch.url.includes('/api/sales/7/payments'));
    assert.ok(!global.__lastFetch.url.includes('/batch'));
  });
});