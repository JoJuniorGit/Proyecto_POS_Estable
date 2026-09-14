import { describe, test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { claimSale, releaseSale } from './salesApi.js';

describe('salesApi claimSale / releaseSale (bloqueo multiterminal)', () => {
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

  test('claimSale_ConAccionCheckout_EnviaPostConBodyAction', async () => {
    await claimSale(5, 'Checkout');
    assert.ok(global.__lastFetch.url.includes('/api/sales/5/claim'));
    assert.strictEqual(global.__lastFetch.config.method, 'POST');
    assert.deepStrictEqual(JSON.parse(global.__lastFetch.config.body), { action: 'Checkout' });
  });

  test('claimSale_ConAccionEditing_EnviaPostConBodyAction', async () => {
    await claimSale(8, 'Editing');
    assert.ok(global.__lastFetch.url.includes('/api/sales/8/claim'));
    assert.strictEqual(global.__lastFetch.config.method, 'POST');
    assert.deepStrictEqual(JSON.parse(global.__lastFetch.config.body), { action: 'Editing' });
  });

  test('releaseSale_SinForce_EnviaForceFalsePorDefecto', async () => {
    await releaseSale(5);
    assert.ok(global.__lastFetch.url.includes('/api/sales/5/release?force=false'));
    assert.strictEqual(global.__lastFetch.config.method, 'POST');
  });

  test('releaseSale_ConForce_EnviaForceTrue', async () => {
    await releaseSale(5, true);
    assert.ok(global.__lastFetch.url.includes('/api/sales/5/release?force=true'));
    assert.strictEqual(global.__lastFetch.config.method, 'POST');
  });

  test('releaseSale_ConForceFalseExplicito_EnviaForceFalse', async () => {
    await releaseSale(7, false);
    assert.ok(global.__lastFetch.url.includes('/api/sales/7/release?force=false'));
  });
});
