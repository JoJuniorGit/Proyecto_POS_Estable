import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  CURRENCY_FORMAT_VALUES,
  THEME_VALUES,
  broadcastStateSync,
  isCrossTabSyncSupported,
  readStoredState,
  resolveRevalidatedValue,
  resolveStateSyncMessage,
  subscribeStateSync,
  subscribeWindowRevalidation,
  writeStoredState,
} from './crossTabStateSync.js';

function createMemoryStorage(initial = {}) {
  const data = new Map(Object.entries(initial));
  return {
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => { data.set(key, String(value)); },
    removeItem: (key) => { data.delete(key); },
  };
}

class MockBroadcastChannel {
  static instances = [];

  constructor(name) {
    this.name = name;
    this.posted = [];
    this.closed = false;
    this.onmessage = null;
    MockBroadcastChannel.instances.push(this);
  }

  postMessage(data) {
    this.posted.push(data);
  }

  close() {
    this.closed = true;
  }

  emit(data) {
    if (this.closed) return;
    this.onmessage?.({ data });
  }
}

function createEventTarget() {
  const listeners = new Map();
  return {
    addEventListener(type, handler) {
      if (!listeners.has(type)) listeners.set(type, new Set());
      listeners.get(type).add(handler);
    },
    removeEventListener(type, handler) {
      listeners.get(type)?.delete(handler);
    },
    dispatch(type) {
      for (const handler of listeners.get(type) || []) handler();
    },
    listenerCount(type) {
      return listeners.get(type)?.size || 0;
    },
  };
}

const originalWindow = globalThis.window;
const originalDocument = globalThis.document;
const originalLocalStorage = globalThis.localStorage;

function restoreGlobals() {
  if (originalWindow === undefined) delete globalThis.window;
  else globalThis.window = originalWindow;

  if (originalDocument === undefined) delete globalThis.document;
  else globalThis.document = originalDocument;

  if (originalLocalStorage === undefined) delete globalThis.localStorage;
  else globalThis.localStorage = originalLocalStorage;
}

describe('crossTabStateSync pure helpers', () => {
  it('1. resolveStateSyncMessage returns the value for a typed message with an allowed value', () => {
    assert.strictEqual(
      resolveStateSyncMessage({ type: 'pos-state-sync', value: 'dark' }, THEME_VALUES),
      'dark'
    );
    assert.strictEqual(
      resolveStateSyncMessage({ type: 'pos-state-sync', value: 'International' }, CURRENCY_FORMAT_VALUES),
      'International'
    );
  });

  it('2. resolveStateSyncMessage rejects foreign message types and values outside the allowlist', () => {
    assert.strictEqual(resolveStateSyncMessage({ type: 'other', value: 'dark' }, THEME_VALUES), null);
    assert.strictEqual(resolveStateSyncMessage({ type: 'pos-state-sync', value: 'neon' }, THEME_VALUES), null);
    assert.strictEqual(resolveStateSyncMessage(null, THEME_VALUES), null);
    assert.strictEqual(resolveStateSyncMessage(undefined, THEME_VALUES), null);
  });

  it('3. resolveRevalidatedValue adopts a valid stored value only when it differs from the current one', () => {
    assert.strictEqual(resolveRevalidatedValue('light', 'dark', THEME_VALUES), 'light');
    assert.strictEqual(resolveRevalidatedValue('dark', 'dark', THEME_VALUES), 'dark');
    assert.strictEqual(resolveRevalidatedValue('neon', 'dark', THEME_VALUES), 'dark');
    assert.strictEqual(resolveRevalidatedValue(null, 'dark', THEME_VALUES), 'dark');
  });

  it('4. isCrossTabSyncSupported is false without a BroadcastChannel-capable window', () => {
    assert.strictEqual(isCrossTabSyncSupported(), false);
  });
});

describe('crossTabStateSync storage helpers', () => {
  beforeEach(() => {
    restoreGlobals();
  });

  afterEach(() => {
    restoreGlobals();
  });

  it('5. readStoredState/writeStoredState round-trip allowed values and ignore invalid ones', () => {
    globalThis.window = { localStorage: createMemoryStorage() };

    assert.strictEqual(readStoredState('pos-theme', THEME_VALUES), null);
    writeStoredState('pos-theme', 'light');
    assert.strictEqual(readStoredState('pos-theme', THEME_VALUES), 'light');

    writeStoredState('pos-theme', 'neon');
    assert.strictEqual(readStoredState('pos-theme', THEME_VALUES), null);
  });

  it('6. storage helpers are SSR-safe when no storage exists', () => {
    delete globalThis.window;
    delete globalThis.localStorage;

    assert.strictEqual(readStoredState('pos-theme', THEME_VALUES), null);
    assert.doesNotThrow(() => writeStoredState('pos-theme', 'dark'));
  });
});

describe('crossTabStateSync BroadcastChannel helpers', () => {
  beforeEach(() => {
    restoreGlobals();
    MockBroadcastChannel.instances = [];
    globalThis.window = {
      localStorage: createMemoryStorage(),
      BroadcastChannel: MockBroadcastChannel,
    };
  });

  afterEach(() => {
    restoreGlobals();
  });

  it('7. broadcastStateSync posts a typed message and closes the channel', () => {
    broadcastStateSync('pos_theme_sync', 'dark');

    const channel = MockBroadcastChannel.instances[0];
    assert.strictEqual(channel.name, 'pos_theme_sync');
    assert.deepStrictEqual(channel.posted, [{ type: 'pos-state-sync', value: 'dark' }]);
    assert.strictEqual(channel.closed, true);
  });

  it('8. subscribeStateSync forwards only valid messages and unsubscribes cleanly', () => {
    const received = [];
    const unsubscribe = subscribeStateSync('pos_theme_sync', THEME_VALUES, (value) => received.push(value));
    const channel = MockBroadcastChannel.instances[0];

    channel.emit({ type: 'pos-state-sync', value: 'light' });
    channel.emit({ type: 'other', value: 'dark' });
    channel.emit({ type: 'pos-state-sync', value: 'neon' });

    assert.deepStrictEqual(received, ['light']);

    unsubscribe();
    assert.strictEqual(channel.closed, true);

    channel.emit({ type: 'pos-state-sync', value: 'dark' });
    assert.deepStrictEqual(received, ['light']);
  });
});

describe('crossTabStateSync window revalidation helper', () => {
  beforeEach(() => {
    restoreGlobals();
  });

  afterEach(() => {
    restoreGlobals();
  });

  it('9. revalidates on focus and on visible visibilitychange only, and detaches listeners', () => {
    const windowTarget = createEventTarget();
    const documentTarget = createEventTarget();
    documentTarget.visibilityState = 'visible';
    globalThis.window = windowTarget;
    globalThis.document = documentTarget;

    let revalidations = 0;
    const unsubscribe = subscribeWindowRevalidation(() => { revalidations += 1; });

    windowTarget.dispatch('focus');
    documentTarget.dispatch('visibilitychange');
    assert.strictEqual(revalidations, 2);

    documentTarget.visibilityState = 'hidden';
    documentTarget.dispatch('visibilitychange');
    assert.strictEqual(revalidations, 2);

    unsubscribe();
    assert.strictEqual(windowTarget.listenerCount('focus'), 0);
    assert.strictEqual(documentTarget.listenerCount('visibilitychange'), 0);
  });
});
