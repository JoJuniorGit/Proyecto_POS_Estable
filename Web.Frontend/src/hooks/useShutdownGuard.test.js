import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  UNLOAD_WARNING_MESSAGE,
  handleBeforeUnloadEvent,
  handleShutdownSequence,
  handleBfcacheRestore,
  shouldRunShutdownOnPageHide,
} from './useShutdownGuard.js';
import { registerShutdownCleanup, resetShutdownCleanups } from '../utils/shutdownRegistry.js';
import { readCleanShutdown, readRecoverySnapshot, saveRecoverySnapshot } from '../utils/saleRecovery.js';

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

describe('useShutdownGuard.js', () => {
  afterEach(() => {
    delete globalThis.window;
    resetShutdownCleanups();
  });

  test('1. handleBeforeUnloadEvent marca el evento y retorna el mensaje solo con estado volátil', () => {
    const volatileEvent = {
      returnValue: '',
      prevented: false,
      preventDefault() {
        this.prevented = true;
      },
    };

    const message = handleBeforeUnloadEvent(volatileEvent, true);

    assert.strictEqual(volatileEvent.prevented, true);
    assert.strictEqual(volatileEvent.returnValue, UNLOAD_WARNING_MESSAGE);
    assert.strictEqual(message, UNLOAD_WARNING_MESSAGE);

    const cleanEvent = {
      returnValue: '',
      prevented: false,
      preventDefault() {
        this.prevented = true;
      },
    };

    assert.strictEqual(handleBeforeUnloadEvent(cleanEvent, false), undefined);
    assert.strictEqual(cleanEvent.prevented, false);
    assert.strictEqual(cleanEvent.returnValue, '');
  });

  test('2. handleShutdownSequence ejecuta flushState aunque lance, marca el cierre del snapshot y corre los cleanups', () => {
    globalThis.window = {
      localStorage: createMemoryStorage(),
      sessionStorage: createMemoryStorage(),
    };

    saveRecoverySnapshot({ saleId: 42, itemCount: 1, status: 'Pending' });
    const snapshot = readRecoverySnapshot();

    const calls = [];
    let cleanShutdownAtCleanup = null;

    registerShutdownCleanup(() => {
      calls.push('cleanup');
      cleanShutdownAtCleanup = readCleanShutdown();
    });

    const flushState = () => {
      calls.push('flush');
      throw new Error('flush failed');
    };

    assert.doesNotThrow(() => handleShutdownSequence({ flushState }));
    assert.deepStrictEqual(calls, ['flush', 'cleanup']);
    assert.strictEqual(cleanShutdownAtCleanup.saleId, 42);
    assert.ok(cleanShutdownAtCleanup.at >= snapshot.savedAt);
  });

  test('2b. handleShutdownSequence no escribe marca de cierre cuando no hay snapshot', () => {
    globalThis.window = {
      localStorage: createMemoryStorage(),
      sessionStorage: createMemoryStorage(),
    };

    assert.doesNotThrow(() => handleShutdownSequence({}));
    assert.strictEqual(globalThis.window.localStorage.getItem('pos_clean_shutdown_at'), null);
    assert.strictEqual(readCleanShutdown(), null);
  });

  test('4. shouldRunShutdownOnPageHide no interrumpe el BFCache y sí permite el cierre real', () => {
    assert.strictEqual(shouldRunShutdownOnPageHide({ persisted: true }), false);
    assert.strictEqual(shouldRunShutdownOnPageHide({ persisted: false }), true);
    assert.strictEqual(shouldRunShutdownOnPageHide(undefined), true);
  });

  test('3. handleBfcacheRestore no lanza sin storage y limpia la marca de cierre con storage', () => {
    delete globalThis.window;
    assert.doesNotThrow(() => handleBfcacheRestore());

    globalThis.window = {
      localStorage: createMemoryStorage(),
      sessionStorage: createMemoryStorage(),
    };
    globalThis.window.localStorage.setItem('pos_clean_shutdown_at', '123');

    assert.doesNotThrow(() => handleBfcacheRestore());
    assert.strictEqual(globalThis.window.localStorage.getItem('pos_clean_shutdown_at'), null);
  });
});
