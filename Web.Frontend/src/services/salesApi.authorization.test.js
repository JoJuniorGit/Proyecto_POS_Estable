import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { addItemToSale } from './salesApi.js';

/**
 * 8.150 (T7): la accion protegida viaja con el token efimero en X-Authorization-Token
 * cuando el flujo de espera lo entrega. Sin token, el contrato previo no cambia.
 */
function jsonResponse(payload, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? 'OK' : 'Forbidden',
    headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'application/json' : null) },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('salesApi addItemToSale authorization token header', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse({ id: 3, items: [] });
    };
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. sends X-Authorization-Token when a token is provided', async () => {
    await addItemToSale(42, 5, 2, 36.5, null, 'ephemeral-token-abc');

    assert.strictEqual(fetchCalls[0].config.headers['X-Authorization-Token'], 'ephemeral-token-abc');
  });

  it('2. omits the header without a token and keeps the existing contract intact', async () => {
    await addItemToSale(42, 5, 2, 36.5);

    const headers = fetchCalls[0].config.headers;
    assert.ok(!('X-Authorization-Token' in headers), 'no token means no authorization header');
    const key = headers['Idempotency-Key'];
    assert.ok(typeof key === 'string' && key.length > 0, 'the Idempotency-Key contract stays intact');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { productId: 5, quantity: 2, exchangeRate: 36.5 });
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
  });

  it('3. forwards the token together with an explicit idempotency key', async () => {
    await addItemToSale(42, 6, 1, 36.5, 'stable-key', 'token-xyz');

    const headers = fetchCalls[0].config.headers;
    assert.strictEqual(headers['Idempotency-Key'], 'stable-key');
    assert.strictEqual(headers['X-Authorization-Token'], 'token-xyz');
  });
});