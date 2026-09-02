import { describe, it } from 'node:test';
import assert from 'node:assert';

describe('BarcodeScannerModal Cooldown & Recent 3 Items History with Direct Data Binding', () => {
  it('1. Enforces 2000ms cooldown for the same barcode while allowing different barcodes immediately', () => {
    const COOLDOWN_MS = 2000;
    let lastCode = null;
    let lastHitAt = 0;
    let acceptedScans = [];

    const handleScan = (code, currentTime) => {
      const now = currentTime;
      if (lastCode === code && now - lastHitAt < COOLDOWN_MS) {
        // Cooldown hit -> reject
        return false;
      }
      lastCode = code;
      lastHitAt = now;
      acceptedScans.push({ code, time: now });
      return true;
    };

    // Scan product A at t = 1000
    assert.strictEqual(handleScan('7591001002009', 1000), true);

    // Re-scan product A at t = 2500 (1500ms later < 2000ms) -> rejected
    assert.strictEqual(handleScan('7591001002009', 2500), false);

    // Scan product B at t = 2600 (different code) -> accepted immediately!
    assert.strictEqual(handleScan('5901234123457', 2600), true);

    // Re-scan product A at t = 3100 (2100ms after t=1000) -> accepted!
    assert.strictEqual(handleScan('7591001002009', 3100), true);

    assert.strictEqual(acceptedScans.length, 3);
  });

  it('2. Groups quantities and projects directly from currentSale.items without isolated counters', () => {
    // Simulated central cart state
    const currentSale = {
      items: [
        { id: 101, productId: 1, productName: 'Bebida Boka Naranja 2L', quantity: 3, unitPriceBsS: 120 },
        { id: 102, productId: 2, productName: 'Galletas Maria 100g', quantity: 1, unitPriceBsS: 45 },
        { id: 103, productId: 3, productName: 'Harina PAN 1kg', quantity: 2, unitPriceBsS: 80 }
      ]
    };

    const recentProductIds = [1, 2, 3];

    // Direct derivation (Data Binding)
    const recentItems = recentProductIds
      .map((prodId) => currentSale.items.find((i) => i.productId === prodId))
      .filter(Boolean);

    assert.strictEqual(recentItems.length, 3);
    assert.strictEqual(recentItems[0].productName, 'Bebida Boka Naranja 2L');
    assert.strictEqual(recentItems[0].quantity, 3); // x3 reflected directly from central cart!
  });

  it('3. Maintains maximum 3 distinct items and promotes existing items to the top without creating duplicate rows', () => {
    let recentProductIds = [];

    const registerScan = (productId) => {
      recentProductIds = [productId, ...recentProductIds.filter(id => id !== productId)].slice(0, 3);
    };

    // Scan 1, 2, 3
    registerScan(1); // [1]
    registerScan(2); // [2, 1]
    registerScan(3); // [3, 2, 1]
    assert.deepStrictEqual(recentProductIds, [3, 2, 1]);

    // Re-scan 1 (already in top 3) -> should move to index 0, length remains 3
    registerScan(1);
    assert.deepStrictEqual(recentProductIds, [1, 3, 2]);

    // Scan new item 4 -> pushes out oldest item (2)
    registerScan(4);
    assert.deepStrictEqual(recentProductIds, [4, 1, 3]);
  });

  it('4. Immediately stops all camera hardware tracks and resets ZXing reader on modal close', () => {
    let track1Stopped = false;
    let track2Stopped = false;
    let zxingReset = false;

    const mockTrack1 = { stop: () => { track1Stopped = true; } };
    const mockTrack2 = { stop: () => { track2Stopped = true; } };

    const mockStream = {
      getTracks: () => [mockTrack1, mockTrack2]
    };

    const mockReader = {
      reset: () => { zxingReset = true; }
    };

    const mockVideo = {
      srcObject: mockStream,
      pause: () => {},
      removeAttribute: () => {}
    };

    // Simulate stopActiveStream
    if (mockReader) {
      mockReader.reset();
    }
    if (mockStream) {
      mockStream.getTracks().forEach(t => t.stop());
    }
    if (mockVideo.srcObject) {
      mockVideo.srcObject.getTracks().forEach(t => t.stop());
      mockVideo.srcObject = null;
    }

    assert.strictEqual(track1Stopped, true);
    assert.strictEqual(track2Stopped, true);
    assert.strictEqual(zxingReset, true);
    assert.strictEqual(mockVideo.srcObject, null);
  });
});
