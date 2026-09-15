import { describe, it } from 'node:test';
import assert from 'node:assert';
import { resolveSaleResponse } from './CartContext.jsx';

describe('CartContext exchange-rate response sequence guard', () => {
  it('1. Applies the response when its request is still the latest', () => {
    const sale = { id: 7, appliedRate: 52, items: [{ id: 1 }] };

    assert.strictEqual(resolveSaleResponse(sale, 4, 4), sale);
  });

  it('2. Ignores a stale response after a newer cart mutation', () => {
    const staleSale = { id: 7, appliedRate: 50, items: [] };

    assert.strictEqual(resolveSaleResponse(staleSale, 4, 5), null);
  });

  it('3. Never applies empty responses', () => {
    assert.strictEqual(resolveSaleResponse(null, 4, 4), null);
    assert.strictEqual(resolveSaleResponse(undefined, 4, 4), null);
  });

  it('4. Full sequence: rate request -> newer mutation -> rate retry applies only the latest', () => {
    let latestRequestId = 0;
    const rateRequestId = ++latestRequestId;

    latestRequestId += 1;
    assert.strictEqual(resolveSaleResponse({ id: 7, appliedRate: 50 }, rateRequestId, latestRequestId), null);

    const freshRateRequestId = ++latestRequestId;
    const freshSale = { id: 7, appliedRate: 52 };

    assert.strictEqual(resolveSaleResponse(freshSale, freshRateRequestId, latestRequestId), freshSale);
  });
});
