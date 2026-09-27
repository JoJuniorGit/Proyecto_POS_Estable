import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  RECOVERY_MAX_AGE_MS,
  readRecoverySnapshot,
  readCleanShutdown,
  canOfferRecovery,
  hasOrphanedSnapshot,
  saveRecoverySnapshot,
  clearRecoverySnapshot,
  markCleanShutdown,
  markSessionDirty,
} from './saleRecovery.js';

const SNAPSHOT_KEY = 'pos_orphan_sale_snapshot';
const CLEAN_SHUTDOWN_KEY = 'pos_clean_shutdown_at';
const ACTIVE_SALE_ID_KEY = 'active_pos_sale_id';

function createMemoryStorage() {
  const store = new Map();
  return {
    getItem(key) {
      return store.has(key) ? store.get(key) : null;
    },
    setItem(key, value) {
      store.set(key, String(value));
    },
    removeItem(key) {
      store.delete(key);
    },
    clear() {
      store.clear();
    },
  };
}

function setupWindow() {
  const localStorage = createMemoryStorage();
  const sessionStorage = createMemoryStorage();
  globalThis.window = { localStorage, sessionStorage };
  return { localStorage, sessionStorage };
}

describe('saleRecovery.js', () => {
  afterEach(() => {
    delete globalThis.window;
  });

  test('1. saveRecoverySnapshot y readRecoverySnapshot hacen roundtrip con savedAt asignado por la función', () => {
    const { localStorage } = setupWindow();
    const before = Date.now();

    saveRecoverySnapshot({
      saleId: 42,
      cashierId: 7,
      cashierName: 'Ana',
      customerName: 'Luis',
      itemCount: 3,
      totalUSD: 15.5,
      status: 'Pending',
      savedAt: 1,
    });

    const snapshot = readRecoverySnapshot();
    const after = Date.now();

    assert.ok(snapshot);
    assert.deepStrictEqual(Object.keys(snapshot).sort(), [
      'cashierId',
      'cashierName',
      'customerName',
      'itemCount',
      'saleId',
      'savedAt',
      'status',
      'totalUSD',
      'version',
    ]);
    assert.strictEqual(snapshot.version, 1);
    assert.strictEqual(snapshot.saleId, 42);
    assert.strictEqual(snapshot.cashierId, 7);
    assert.strictEqual(snapshot.cashierName, 'Ana');
    assert.strictEqual(snapshot.customerName, 'Luis');
    assert.strictEqual(snapshot.itemCount, 3);
    assert.strictEqual(snapshot.totalUSD, 15.5);
    assert.strictEqual(snapshot.status, 'Pending');
    assert.strictEqual(typeof snapshot.savedAt, 'number');
    assert.ok(snapshot.savedAt >= before && snapshot.savedAt <= after);
    assert.ok(localStorage.getItem(SNAPSHOT_KEY));
  });

  test('2. saveRecoverySnapshot no escribe cuando falta saleId', () => {
    const { localStorage } = setupWindow();

    saveRecoverySnapshot({ itemCount: 1, status: 'Pending' });
    saveRecoverySnapshot(null);
    saveRecoverySnapshot({ saleId: 0, itemCount: 1, status: 'Pending' });

    assert.strictEqual(localStorage.getItem(SNAPSHOT_KEY), null);
    assert.strictEqual(readRecoverySnapshot(), null);
  });

  test('3. canOfferRecovery acepta solo snapshots vigentes y completos', () => {
    const now = 1_700_000_000_000;
    const valid = { saleId: 1, itemCount: 2, status: 'Pending', savedAt: now - 1000 };

    assert.strictEqual(canOfferRecovery(valid, now), true);
    assert.strictEqual(canOfferRecovery(null, now), false);
    assert.strictEqual(canOfferRecovery(undefined, now), false);
    assert.strictEqual(canOfferRecovery('snapshot', now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, saleId: 0 }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, saleId: 'abc' }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, itemCount: 0 }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, itemCount: -2 }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, status: 'OnHold' }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, savedAt: 0 }, now), false);
    assert.strictEqual(canOfferRecovery({ ...valid, savedAt: 'ayer' }, now), false);
    assert.strictEqual(
      canOfferRecovery({ ...valid, savedAt: now - RECOVERY_MAX_AGE_MS - 1 }, now),
      false
    );
    assert.strictEqual(canOfferRecovery({ ...valid, savedAt: now - RECOVERY_MAX_AGE_MS }, now), true);
  });

  test('4. readRecoverySnapshot devuelve null ante JSON corrupto', () => {
    const { localStorage } = setupWindow();
    localStorage.setItem(SNAPSHOT_KEY, '{corrupto');

    assert.strictEqual(readRecoverySnapshot(), null);
  });

  test('5. hasOrphanedSnapshot es true con snapshot válido sin marca de cierre', () => {
    setupWindow();
    saveRecoverySnapshot({ saleId: 5, itemCount: 1, status: 'Pending' });

    assert.strictEqual(hasOrphanedSnapshot(Date.now()), true);
  });

  test('6. hasOrphanedSnapshot es false si la marca de cierre es del mismo saleId y posterior al savedAt', () => {
    setupWindow();
    saveRecoverySnapshot({ saleId: 5, itemCount: 1, status: 'Pending' });
    const snapshot = readRecoverySnapshot();
    markCleanShutdown(snapshot.saleId, snapshot.savedAt + 1);

    assert.strictEqual(hasOrphanedSnapshot(snapshot.savedAt + 2), false);
  });

  test('6b. hasOrphanedSnapshot es true cuando la marca de cierre es de otro saleId', () => {
    setupWindow();
    saveRecoverySnapshot({ saleId: 42, itemCount: 2, status: 'Pending' });
    const snapshot = readRecoverySnapshot();
    markCleanShutdown(99, snapshot.savedAt + 5);

    assert.strictEqual(hasOrphanedSnapshot(snapshot.savedAt + 10), true);
  });

  test('6c. markCleanShutdown con saleId null no suprime el snapshot', () => {
    setupWindow();
    saveRecoverySnapshot({ saleId: 42, itemCount: 2, status: 'Pending' });
    const snapshot = readRecoverySnapshot();
    markCleanShutdown(null, snapshot.savedAt + 5);

    assert.deepStrictEqual(readCleanShutdown(), { saleId: null, at: snapshot.savedAt + 5 });
    assert.strictEqual(hasOrphanedSnapshot(snapshot.savedAt + 10), true);
  });

  test('6d. hasOrphanedSnapshot es true si la marca de cierre del mismo saleId es anterior al savedAt', () => {
    setupWindow();
    saveRecoverySnapshot({ saleId: 42, itemCount: 2, status: 'Pending' });
    const snapshot = readRecoverySnapshot();
    markCleanShutdown(42, snapshot.savedAt - 5);

    assert.strictEqual(hasOrphanedSnapshot(snapshot.savedAt + 10), true);
  });

  test('6e. readCleanShutdown devuelve null ante valor numérico legado y JSON corrupto', () => {
    const { localStorage } = setupWindow();
    localStorage.setItem(CLEAN_SHUTDOWN_KEY, '1700000000000');
    assert.strictEqual(readCleanShutdown(), null);

    localStorage.setItem(CLEAN_SHUTDOWN_KEY, '{corrupto');
    assert.strictEqual(readCleanShutdown(), null);
  });

  test('7. hasOrphanedSnapshot es false si sessionStorage registra la venta activa', () => {
    const { sessionStorage } = setupWindow();
    saveRecoverySnapshot({ saleId: 5, itemCount: 1, status: 'Pending' });
    sessionStorage.setItem(ACTIVE_SALE_ID_KEY, '5');

    assert.strictEqual(hasOrphanedSnapshot(Date.now()), false);

    sessionStorage.setItem(ACTIVE_SALE_ID_KEY, '9');
    assert.strictEqual(hasOrphanedSnapshot(Date.now()), true);
  });

  test('8. clearRecoverySnapshot conserva la marca de cierre y markSessionDirty la elimina', () => {
    const { localStorage } = setupWindow();
    saveRecoverySnapshot({ saleId: 5, itemCount: 1, status: 'Pending' });
    markCleanShutdown(5, 123456);

    clearRecoverySnapshot();
    assert.strictEqual(readRecoverySnapshot(), null);
    assert.deepStrictEqual(readCleanShutdown(), { saleId: 5, at: 123456 });

    markSessionDirty();
    assert.strictEqual(readCleanShutdown(), null);
    assert.strictEqual(localStorage.getItem(CLEAN_SHUTDOWN_KEY), null);
  });

  test('9. Ninguna función lanza cuando window existe sin storage', () => {
    globalThis.window = {};

    assert.doesNotThrow(() => readRecoverySnapshot());
    assert.doesNotThrow(() => readCleanShutdown());
    assert.strictEqual(canOfferRecovery(null), false);
    assert.doesNotThrow(() => hasOrphanedSnapshot());
    assert.doesNotThrow(() => saveRecoverySnapshot({ saleId: 1, itemCount: 1 }));
    assert.doesNotThrow(() => clearRecoverySnapshot());
    assert.doesNotThrow(() => markCleanShutdown());
    assert.doesNotThrow(() => markSessionDirty());
  });

  test('10. Ninguna función lanza cuando window no existe', () => {
    delete globalThis.window;

    assert.doesNotThrow(() => readRecoverySnapshot());
    assert.doesNotThrow(() => readCleanShutdown());
    assert.doesNotThrow(() => hasOrphanedSnapshot());
    assert.doesNotThrow(() => saveRecoverySnapshot({ saleId: 1, itemCount: 1 }));
    assert.doesNotThrow(() => clearRecoverySnapshot());
    assert.doesNotThrow(() => markCleanShutdown());
    assert.doesNotThrow(() => markSessionDirty());
  });
});
