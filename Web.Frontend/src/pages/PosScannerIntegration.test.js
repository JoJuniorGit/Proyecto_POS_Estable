import { describe, it } from 'node:test';
import assert from 'node:assert';
import { isValidBarcode } from '../utils/barcodeValidator.js';

describe('Pos Scanner Pipeline Integration with AbortController', () => {
  it('1. Burst scanning cancels pending in-flight request via AbortController and executes only the latest scan', async () => {
    let abortController = null;
    const addedItems = [];

    // Mock resolve function that respects signal
    const mockGetProductBySku = (code, signal) => {
      return new Promise((resolve, reject) => {
        const timeout = setTimeout(() => {
          if (signal?.aborted) {
            const err = new Error('The operation was aborted');
            err.name = 'AbortError';
            reject(err);
          } else {
            resolve({ id: 100, sku: code, name: `Product ${code}`, priceUSD: 10, isActive: true });
          }
        }, code === '7591001002009' ? 50 : 10); // First code takes 50ms, second takes 10ms

        signal?.addEventListener('abort', () => {
          clearTimeout(timeout);
          const err = new Error('The operation was aborted');
          err.name = 'AbortError';
          reject(err);
        });
      });
    };

    const handleScannedCode = async (code) => {
      if (!isValidBarcode(code)) return;

      abortController?.abort();
      const controller = new AbortController();
      abortController = controller;

      try {
        const product = await mockGetProductBySku(code, controller.signal);
        if (controller.signal.aborted) return;
        if (product?.id) {
          addedItems.push(product);
        }
      } catch (err) {
        if (err?.name === 'AbortError' || controller.signal.aborted) {
          return;
        }
        throw err;
      }
    };

    // Trigger rapid burst scan: First code 1, then code 2 before code 1 resolves
    const scan1Promise = handleScannedCode('7591001002009');
    const scan2Promise = handleScannedCode('036000291452');

    await Promise.all([scan1Promise, scan2Promise]);

    // Only the second scan must be added to the cart
    assert.strictEqual(addedItems.length, 1);
    assert.strictEqual(addedItems[0].sku, '036000291452');
  });
});
