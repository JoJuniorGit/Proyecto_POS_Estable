// 8.211-T4b1 (S9): contrato HTTP del perfil monetario de clientes. El wrapper solo consulta
// la ruta read-only autenticada y devuelve el DTO tal como llega.
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { getMonetaryProfile } from './monetaryProfileApi.js';

function jsonResponse(payload) {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    headers: { get: (header) => (header.toLowerCase() === 'content-type' ? 'application/json' : null) },
    text: async () => JSON.stringify(payload),
    json: async () => payload,
  };
}

describe('monetaryProfileApi', () => {
  let originalFetch;
  let fetchCalls;

  beforeEach(() => {
    originalFetch = global.fetch;
    fetchCalls = [];
  });

  afterEach(() => {
    global.fetch = originalFetch;
  });

  it('1. GETs the authenticated client profile route and returns the DTO', async () => {
    const payload = {
      mode: 1,
      primaryCurrencyCode: 'USD',
      primaryCurrencySymbol: '$',
      secondaryCurrencyCode: 'VES',
      secondaryCurrencySymbol: 'Bs.S',
      isConfigured: true,
    };
    global.fetch = async (url, config) => {
      fetchCalls.push({ url, config });
      return jsonResponse(payload);
    };

    const result = await getMonetaryProfile();

    assert.strictEqual(fetchCalls.length, 1);
    assert.strictEqual(fetchCalls[0].url, 'http://localhost:5000/api/client/monetary-profile');
    assert.strictEqual(fetchCalls[0].config.method, 'GET');
    assert.deepStrictEqual(result, payload);
  });

  it('2. forwards the caller abort signal through the shared api client', async () => {
    const controller = new AbortController();
    let observedSignal = null;
    global.fetch = async (_url, config) => {
      observedSignal = config.signal;
      return jsonResponse({});
    };

    await getMonetaryProfile(controller.signal);

    assert.ok(observedSignal);
    assert.strictEqual(typeof observedSignal.addEventListener, 'function');
    assert.strictEqual(observedSignal.aborted, false);
  });
});
