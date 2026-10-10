import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  FORCE_DISCONNECT_EVENT,
  SESSION_REVOKED_EVENT,
  connectRateHub,
  disconnectRateHub,
  handleForceDisconnect,
  notifySessionRevoked,
} from './signalr.js';

const flushMicrotasks = () => new Promise((resolve) => { setTimeout(resolve, 0); });

function createFakeConnection() {
  const handlers = new Map();
  const connection = {
    state: 'Disconnected',
    starts: 0,
    stops: 0,
    offCalls: [],
    async start() {
      connection.starts += 1;
      connection.state = 'Connected';
    },
    async stop() {
      connection.stops += 1;
      connection.state = 'Disconnected';
    },
    on(name, handler) {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(handler);
    },
    off(name, handler) {
      connection.offCalls.push(name);
      const set = handlers.get(name);
      if (!set) return;
      if (handler) set.delete(handler);
      else set.clear();
    },
    emit(name, payload) {
      for (const handler of [...(handlers.get(name) ?? [])]) handler(payload);
    },
    handlerCount(name) {
      return handlers.get(name)?.size ?? 0;
    },
  };
  return connection;
}

describe('signalr ForceDisconnect handler', () => {
  it('1. registers via off/on and stops the rate hub exactly once with the revoke warning', async () => {
    const connection = createFakeConnection();
    const warnings = [];
    let revoked = 0;

    handleForceDisconnect(connection, {
      logger: { warn: (message) => warnings.push(message) },
      onRevoked: () => { revoked += 1; },
    });

    assert.deepStrictEqual(connection.offCalls, [FORCE_DISCONNECT_EVENT]);
    assert.strictEqual(connection.handlerCount(FORCE_DISCONNECT_EVENT), 1);

    connection.emit(FORCE_DISCONNECT_EVENT);
    await flushMicrotasks();

    assert.strictEqual(connection.stops, 1);
    assert.strictEqual(connection.starts, 0, 'the explicit stop must not trigger a reconnect');
    assert.deepStrictEqual(warnings, ['[SignalR] Sesión revocada por el servidor; cerrando conexión de tasa en tiempo real.']);
    assert.strictEqual(revoked, 1);
  });

  it('2. re-registering does not duplicate the handler nor the stop', async () => {
    const connection = createFakeConnection();
    let revoked = 0;
    const options = { logger: { warn: () => {} }, onRevoked: () => { revoked += 1; } };

    handleForceDisconnect(connection, options);
    handleForceDisconnect(connection, options);

    assert.strictEqual(connection.handlerCount(FORCE_DISCONNECT_EVENT), 1);

    connection.emit(FORCE_DISCONNECT_EVENT);
    await flushMicrotasks();

    assert.strictEqual(connection.stops, 1);
    assert.strictEqual(revoked, 1);
  });

  it('3. notifies with the existing pos_unauthorized session event', () => {
    const dispatched = [];
    globalThis.window = { dispatchEvent: (event) => dispatched.push(event) };
    try {
      notifySessionRevoked();
    } finally {
      delete globalThis.window;
    }

    assert.strictEqual(SESSION_REVOKED_EVENT, 'pos_unauthorized');
    assert.strictEqual(dispatched.length, 1);
    assert.ok(dispatched[0] instanceof CustomEvent);
    assert.strictEqual(dispatched[0].type, SESSION_REVOKED_EVENT);
  });
});

describe('signalr connectRateHub ForceDisconnect integration', () => {
  it('4. clears the stopped module connection so a later connectRateHub rebuilds', async () => {
    const first = createFakeConnection();
    const onRate = () => {};

    await connectRateHub(onRate, undefined, undefined, { connectionFactory: () => first });

    assert.strictEqual(first.starts, 1);
    assert.strictEqual(first.handlerCount('ReceiveRateUpdate'), 1);
    assert.strictEqual(first.handlerCount(FORCE_DISCONNECT_EVENT), 1);

    // Un segundo connectRateHub con la conexión viva re-registra sin duplicar ni reconstruir.
    await connectRateHub(onRate, undefined, undefined, {
      connectionFactory: () => { throw new Error('no debe reconstruirse mientras la conexión vive'); },
    });
    assert.strictEqual(first.handlerCount('ReceiveRateUpdate'), 1);
    assert.strictEqual(first.handlerCount(FORCE_DISCONNECT_EVENT), 1);

    const dispatched = [];
    globalThis.window = { dispatchEvent: (event) => dispatched.push(event) };
    try {
      first.emit(FORCE_DISCONNECT_EVENT);
      await flushMicrotasks();

      assert.strictEqual(first.stops, 1);
      assert.strictEqual(dispatched.length, 1);
      assert.strictEqual(dispatched[0].type, SESSION_REVOKED_EVENT);

      const second = createFakeConnection();
      let rebuilds = 0;
      await connectRateHub(onRate, undefined, undefined, {
        connectionFactory: () => { rebuilds += 1; return second; },
      });

      assert.strictEqual(rebuilds, 1, 'the module connection must be released after the forced stop');
      assert.strictEqual(second.starts, 1);
      assert.strictEqual(second.handlerCount('ReceiveRateUpdate'), 1);
    } finally {
      delete globalThis.window;
      await disconnectRateHub();
    }
  });
});
