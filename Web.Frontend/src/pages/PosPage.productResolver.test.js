import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert';
import { resolveProductRefDeduped, clearProductRefCache } from './PosPage.jsx';

describe('PosPage scanned-code resolver dedupe', () => {
  beforeEach(() => clearProductRefCache());

  it('1. Shares a single in-flight request between the cart flow and the scanner panel', async () => {
    let calls = 0;
    const resolve = async (code) => {
      calls++;
      return { id: 7, sku: code };
    };

    const cartRequest = resolveProductRefDeduped('7591001002009', { resolve, now: 1000 });
    const panelRequest = resolveProductRefDeduped('7591001002009', { resolve, now: 1001 });

    assert.strictEqual(calls, 1);
    assert.strictEqual(cartRequest, panelRequest);
    assert.deepStrictEqual(await cartRequest, { id: 7, sku: '7591001002009' });
    assert.deepStrictEqual(await panelRequest, { id: 7, sku: '7591001002009' });
    assert.strictEqual(calls, 1);
  });

  it('2. Issues a new request once the short-lived window expires', async () => {
    let calls = 0;
    const resolve = async (code) => {
      calls++;
      return { id: calls, sku: code };
    };

    await resolveProductRefDeduped('7591001002009', { resolve, now: 1000 });
    await resolveProductRefDeduped('7591001002009', { resolve, now: 1500 });
    assert.strictEqual(calls, 1);

    await resolveProductRefDeduped('7591001002009', { resolve, now: 2300 });
    assert.strictEqual(calls, 2);
  });

  it('3. Keeps independent entries per scanned code', async () => {
    const resolvedSkus = [];
    const resolve = async (code) => {
      resolvedSkus.push(code);
      return { id: resolvedSkus.length, sku: code };
    };

    const first = await resolveProductRefDeduped('7591001002009', { resolve, now: 1000 });
    const second = await resolveProductRefDeduped('036000291452', { resolve, now: 1000 });

    assert.strictEqual(first.sku, '7591001002009');
    assert.strictEqual(second.sku, '036000291452');
    assert.deepStrictEqual(resolvedSkus, ['7591001002009', '036000291452']);
  });

  it('4. clearProductRefCache invalidates the short-lived memo', async () => {
    let calls = 0;
    const resolve = async (code) => {
      calls++;
      return { id: calls, sku: code };
    };

    resolveProductRefDeduped('7591001002009', { resolve, now: 1000 });
    clearProductRefCache();
    resolveProductRefDeduped('7591001002009', { resolve, now: 1001 });

    assert.strictEqual(calls, 2);
  });
});
