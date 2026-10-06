import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { addItemToSale } from './salesApi.js';

/**
 * 8.149 (SRE-02/D4): POST /api/sales/{id}/items exige Idempotency-Key en el backend.
 * La clave se genera por intento logico y api.js reutiliza la MISMA clave en su
 * reintento interno (solo reintenta POST con clave), de modo que un microcorte no
 * duplica el item.
 */
function jsonResponse(payload, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? 'OK' : 'Service Unavailable',
    headers: { get: (h) => (h.toLowerCase() === 'content-type' ? 'application/json' : null) },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('salesApi addItemToSale stable Idempotency-Key per add attempt', () => {
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

  it('1. POST items sends a non-empty Idempotency-Key and the unchanged payload', async () => {
    const sale = await addItemToSale(42, 5, 2, 36.5);

    assert.strictEqual(fetchCalls.length, 1);
    assert.ok(fetchCalls[0].url.includes('/api/sales/42/items'));
    assert.strictEqual(fetchCalls[0].config.method, 'POST');
    const key = fetchCalls[0].config.headers['Idempotency-Key'];
    assert.ok(typeof key === 'string' && key.length > 0, 'add-item must send a non-empty Idempotency-Key');
    assert.deepStrictEqual(JSON.parse(fetchCalls[0].config.body), { productId: 5, quantity: 2, exchangeRate: 36.5 });
    assert.strictEqual(sale.id, 3);
  });

  it('2. Each logical add generates a fresh key', async () => {
    await addItemToSale(42, 5, 2, 36.5);
    await addItemToSale(42, 6, 1, 36.5);

    const firstKey = fetchCalls[0].config.headers['Idempotency-Key'];
    const secondKey = fetchCalls[1].config.headers['Idempotency-Key'];
    assert.ok(firstKey && firstKey.length > 0, 'first add must send a key');
    assert.ok(secondKey && secondKey.length > 0, 'second add must send a key');
    assert.notStrictEqual(secondKey, firstKey, 'a new logical add must not reuse the previous key');
  });

  it('3. Retry of the same attempt reuses the same key (api.js keyed retry)', async () => {
    let calls = 0;
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      calls++;
      return calls === 1 ? jsonResponse({}, 503) : jsonResponse({ id: 3, items: [] });
    };

    await addItemToSale(42, 5, 2, 36.5);

    assert.strictEqual(fetchCalls.length, 2, 'api.js must retry the keyed POST once on 503');
    const firstKey = fetchCalls[0].config.headers['Idempotency-Key'];
    assert.ok(firstKey && firstKey.length > 0, 'the retried POST must carry the key');
    assert.strictEqual(fetchCalls[1].config.headers['Idempotency-Key'], firstKey);
  });

  it('4. Forwards an explicit caller-provided key verbatim', async () => {
    await addItemToSale(42, 5, 2, 36.5, 'add-attempt-stable-key');

    assert.strictEqual(fetchCalls[0].config.headers['Idempotency-Key'], 'add-attempt-stable-key');
  });
});
