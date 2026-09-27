import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { holdSale } from './salesApi.js';
import { createCheckoutKeyHolder } from '../utils/idempotency.js';

function jsonResponse(payload) {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('salesApi holdSale stable Idempotency-Key per hold attempt', () => {
  let originalFetch;
  let fetchCalls;

  const request = { customerId: 3, exchangeRate: 50, initialPayments: null };

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 5 });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. Retries of the same attempt reuse the SAME key (server-side dedupe)', async () => {
    const holder = createCheckoutKeyHolder();

    await holdSale(77, request, 0, null, holder.getOrCreateKey());
    await holdSale(77, request, 0, null, holder.getOrCreateKey());

    assert.strictEqual(fetchCalls.length, 2);
    const firstKey = fetchCalls[0].config.headers['Idempotency-Key'];
    const retryKey = fetchCalls[1].config.headers['Idempotency-Key'];
    assert.ok(firstKey && firstKey.length > 0, 'a key must be generated for the attempt');
    assert.strictEqual(retryKey, firstKey, 'a network retry of the same attempt must reuse the key');
  });

  it('2. After reset (success/discard/close) the next hold generates a NEW key', async () => {
    const holder = createCheckoutKeyHolder();

    await holdSale(77, request, 0, null, holder.getOrCreateKey());
    const firstKey = fetchCalls[0].config.headers['Idempotency-Key'];

    holder.reset();
    await holdSale(77, request, 0, null, holder.getOrCreateKey());
    const nextKey = fetchCalls[1].config.headers['Idempotency-Key'];

    assert.ok(nextKey && nextKey.length > 0);
    assert.notStrictEqual(nextKey, firstKey, 'a new attempt must not reuse the previous key');
  });

  it('3. Forwards the caller-provided key verbatim on the hold endpoint', async () => {
    await holdSale(9, request, 0, null, 'hold-attempt-stable-key');

    assert.ok(fetchCalls[0].url.includes('/api/sales/9/hold'));
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'hold-attempt-stable-key');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), request);
  });

  it('4. Legacy positional contract still builds the payload and generates a key when omitted', async () => {
    await holdSale(12, 44, 36.5, { paymentMethodId: 2, amountBsS: 100 });

    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), {
      customerId: 44,
      exchangeRate: 36.5,
      initialPayment: { paymentMethodId: 2, amountBsS: 100 },
    });
    assert.ok(fetchCalls[0].config.headers['Idempotency-Key'], 'fallback key must exist');
  });
});
