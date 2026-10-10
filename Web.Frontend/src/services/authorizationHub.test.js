import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  AUTHORIZATION_EVENT_NAMES,
  buildAuthorizationRequestPayload,
  createAuthorizationHubClient,
  createDefaultConnectionFactory,
} from './authorizationHub.js';
import { ApiError } from './api.js';

const CONTEXT = {
  saleId: 445,
  productId: 5,
  productName: 'Café molido',
  quantity: 2,
  customUnitPriceUsd: 1.5,
  customUnitPriceLocal: 55.5,
  terminal: 'Caja-01',
};

function createFakeConnection() {
  const handlers = new Map();
  const reconnectedHandlers = new Set();
  const connection = {
    state: 'Disconnected',
    invokes: [],
    starts: 0,
    stops: 0,
    invokeImpl: async () => ({ success: true, requestId: 7 }),
    async start() {
      connection.starts += 1;
      connection.state = 'Connected';
    },
    async stop() {
      connection.stops += 1;
      connection.state = 'Disconnected';
    },
    async invoke(name, ...args) {
      connection.invokes.push({ name, args });
      return connection.invokeImpl(name, args);
    },
    on(name, handler) {
      if (!handlers.has(name)) handlers.set(name, new Set());
      handlers.get(name).add(handler);
    },
    off(name, handler) {
      handlers.get(name)?.delete(handler);
    },
    onreconnected(handler) {
      reconnectedHandlers.add(handler);
    },
    emit(name, payload) {
      for (const handler of handlers.get(name) ?? []) handler(payload);
    },
    fireReconnected() {
      for (const handler of reconnectedHandlers) handler();
    },
    handlerCount(name) {
      return handlers.get(name)?.size ?? 0;
    },
  };
  return connection;
}

function buildClient(options = {}) {
  const connection = options.connection ?? createFakeConnection();
  const calls = { factory: [], posts: [], gets: [] };
  const client = createAuthorizationHubClient({
    connectionFactory: (spec) => {
      calls.factory.push(spec);
      return connection;
    },
    apiPost: async (endpoint, body) => {
      calls.posts.push({ endpoint, body });
      return options.postResponse ? options.postResponse(endpoint, body) : {};
    },
    apiGet: async (endpoint) => {
      calls.gets.push({ endpoint });
      return options.getResponse ? options.getResponse(endpoint) : {};
    },
    getBaseUrlFn: () => 'http://test.local',
    logger: { warn: () => {} },
  });
  return { client, connection, calls };
}

describe('authorizationHub payload builder', () => {
  it('1. maps only the hub request contract fields', () => {
    const payload = buildAuthorizationRequestPayload({ ...CONTEXT, ignored: 'nope' });

    assert.deepStrictEqual(payload, {
      saleId: 445,
      productId: 5,
      productName: 'Café molido',
      quantity: 2,
      customUnitPriceUsd: 1.5,
      customUnitPriceLocal: 55.5,
      terminal: 'Caja-01',
    });
    assert.deepStrictEqual(Object.keys(payload).sort(), [
      'customUnitPriceLocal',
      'customUnitPriceUsd',
      'productId',
      'productName',
      'quantity',
      'saleId',
      'terminal',
    ]);
  });

  it('1b. turns a missing context into explicit nulls, never undefined', () => {
    const payload = buildAuthorizationRequestPayload();

    assert.deepStrictEqual(payload, {
      saleId: null,
      productId: null,
      productName: null,
      quantity: null,
      customUnitPriceUsd: null,
      customUnitPriceLocal: null,
      terminal: null,
    });
    assert.ok(Object.values(payload).every((value) => value === null));
  });
});

describe('authorizationHub connection conventions', () => {
  it('2. builds /hubs/authorization with cookie auth, auto-reconnect and warning logging', () => {
    const builderCalls = [];
    const fakeSignalR = {
      LogLevel: { Warning: 2 },
      HubConnectionBuilder: class {
        withUrl(url, options) {
          builderCalls.push(['withUrl', url, options]);
          return this;
        }
        withAutomaticReconnect(delays) {
          builderCalls.push(['withAutomaticReconnect', delays]);
          return this;
        }
        configureLogging(level) {
          builderCalls.push(['configureLogging', level]);
          return this;
        }
        build() {
          return { state: 'Disconnected' };
        }
      },
    };

    const factory = createDefaultConnectionFactory({ signalRModule: fakeSignalR });
    factory({ url: 'http://test.local/hubs/authorization', options: { withCredentials: true, reconnectDelays: [0, 2000, 5000, 10000, 30000] } });

    assert.deepStrictEqual(builderCalls[0], ['withUrl', 'http://test.local/hubs/authorization', { withCredentials: true }]);
    assert.deepStrictEqual(builderCalls[1], ['withAutomaticReconnect', [0, 2000, 5000, 10000, 30000]]);
    assert.deepStrictEqual(builderCalls[2], ['configureLogging', 2]);
  });
});

describe('authorizationHub requestAuthorization', () => {
  it('3. invokes RequestAuthorization on the hub and normalizes the result', async () => {
    const { client, connection, calls } = buildClient();
    connection.invokeImpl = async () => ({
      success: true,
      requestId: 7,
      status: 'Pending',
      expiresAt: '2026-10-08T12:00:30.000Z',
      remainingLifetimeSeconds: 60,
      deduplicated: false,
    });

    const result = await client.requestAuthorization(CONTEXT);

    assert.strictEqual(calls.factory[0].url, 'http://test.local/hubs/authorization');
    assert.strictEqual(calls.factory[0].options.withCredentials, true);
    assert.deepStrictEqual(connection.invokes[0], {
      name: 'RequestAuthorization',
      args: [buildAuthorizationRequestPayload(CONTEXT)],
    });
    assert.deepStrictEqual(result, {
      requestId: 7,
      expiresAt: '2026-10-08T12:00:30.000Z',
      status: 'Pending',
      deduplicated: false,
      remainingLifetimeSeconds: 60,
    });
    assert.strictEqual(calls.posts.length, 0);
  });

  it('4. falls back to POST /api/authorizations when the socket is unavailable', async () => {
    const { client, connection, calls } = buildClient({
      postResponse: async () => ({
        requestId: 9,
        status: 'Pending',
        expiresAt: '2026-10-08T12:00:55.000Z',
        remainingLifetimeSeconds: 55,
        deduplicated: true,
      }),
    });
    connection.start = async () => {
      connection.state = 'Disconnected';
      throw new Error('socket down');
    };

    const result = await client.requestAuthorization(CONTEXT);

    assert.strictEqual(connection.invokes.length, 0);
    assert.strictEqual(calls.posts.length, 1);
    assert.strictEqual(calls.posts[0].endpoint, '/api/authorizations');
    assert.deepStrictEqual(calls.posts[0].body, buildAuthorizationRequestPayload(CONTEXT));
    assert.deepStrictEqual(result, {
      requestId: 9,
      expiresAt: '2026-10-08T12:00:55.000Z',
      status: 'Pending',
      deduplicated: true,
      remainingLifetimeSeconds: 55,
    });
  });

  it('5. falls back to REST when the hub invoke rejects mid-flight', async () => {
    const { client, connection, calls } = buildClient({
      postResponse: async () => ({ requestId: 11, status: 'Pending', expiresAt: null, deduplicated: false }),
    });
    connection.invokeImpl = async () => {
      throw new Error('connection lost');
    };

    const result = await client.requestAuthorization(CONTEXT);

    assert.strictEqual(calls.posts.length, 1);
    assert.strictEqual(result.requestId, 11);
  });

  it('6. surfaces the exact hub refusal message instead of falling back', async () => {
    const { client, connection, calls } = buildClient();
    connection.invokeImpl = async () => ({ success: false, message: 'Los usuarios con rol de Administrador o Supervisor no requieren autorización.' });

    await assert.rejects(
      () => client.requestAuthorization(CONTEXT),
      (error) => error instanceof ApiError && error.message === 'Los usuarios con rol de Administrador o Supervisor no requieren autorización.'
    );
    assert.strictEqual(calls.posts.length, 0);
  });
});

describe('authorizationHub resolveAuthorization and localResolve', () => {
  it('7. invokes ResolveAuthorization with (requestId, approved, reason)', async () => {
    const { client, connection } = buildClient();
    connection.invokeImpl = async () => ({ success: true, status: 'Approved', resolvedByName: 'Admin Dos', reason: 'ok' });

    const result = await client.resolveAuthorization(7, true, null);

    assert.deepStrictEqual(connection.invokes[0], { name: 'ResolveAuthorization', args: [7, true, null] });
    assert.deepStrictEqual(result, { success: true, status: 'Approved', resolvedByName: 'Admin Dos', reason: 'ok' });
  });

  it('8. surfaces the exact race message when a late resolve loses', async () => {
    const { client, connection } = buildClient();
    connection.invokeImpl = async () => ({
      success: false,
      status: 'Approved',
      resolvedByName: 'Admin Uno',
      message: 'Esta solicitud ya fue resuelta por Admin Uno.',
    });

    await assert.rejects(
      () => client.resolveAuthorization(7, false, 'Tarde'),
      (error) => error instanceof ApiError && error.message === 'Esta solicitud ya fue resuelta por Admin Uno.'
    );
  });

  it('8b. falls back to POST /{id}/resolve when the socket invoke fails', async () => {
    const { client, connection, calls } = buildClient({
      postResponse: async () => ({ requestId: 7, status: 'Approved', resolvedByName: 'Admin Dos', reason: null }),
    });
    connection.invokeImpl = async () => {
      throw new Error('connection lost');
    };

    const result = await client.resolveAuthorization(7, true, null);

    assert.strictEqual(connection.invokes.length, 1);
    assert.strictEqual(calls.posts[0].endpoint, '/api/authorizations/7/resolve');
    assert.deepStrictEqual(calls.posts[0].body, { approved: true, reason: null });
    assert.deepStrictEqual(result, { success: true, status: 'Approved', resolvedByName: 'Admin Dos', reason: null });
  });

  it('9. localResolve always posts credentials to the local-resolve REST route', async () => {
    const { client, connection, calls } = buildClient({
      postResponse: async () => ({ token: 'local-token', supervisorUserId: 80, supervisorName: 'Supervisora 80', status: 'Approved' }),
    });

    const result = await client.localResolve(7, { username: 'supervisora', password: 'secreto', reason: null });

    assert.strictEqual(connection.invokes.length, 0);
    assert.strictEqual(calls.posts[0].endpoint, '/api/authorizations/7/local-resolve');
    assert.deepStrictEqual(calls.posts[0].body, { username: 'supervisora', password: 'secreto', reason: null });
    assert.strictEqual(result.token, 'local-token');
    assert.strictEqual(result.supervisorName, 'Supervisora 80');
  });

  it('10. getStatus reads the status REST route', async () => {
    const { client, calls } = buildClient({
      getResponse: async () => ({ id: 7, status: 'Approved', token: 'recovered' }),
    });

    const status = await client.getStatus(7);

    assert.strictEqual(calls.gets[0].endpoint, '/api/authorizations/7');
    assert.deepStrictEqual(status, { id: 7, status: 'Approved', token: 'recovered' });
  });
});

describe('authorizationHub event wiring', () => {
  it('11. subscribes events on connect and unsubscribes cleanly', async () => {
    const { client, connection } = buildClient();
    const received = [];
    const off = client.on(AUTHORIZATION_EVENT_NAMES.resolved, (payload) => received.push(payload));

    await client.connect();
    assert.strictEqual(connection.handlerCount(AUTHORIZATION_EVENT_NAMES.resolved), 1);

    connection.emit(AUTHORIZATION_EVENT_NAMES.resolved, { requestId: 7, status: 'Approved' });
    assert.strictEqual(received.length, 1);

    off();
    connection.emit(AUTHORIZATION_EVENT_NAMES.resolved, { requestId: 7, status: 'Approved' });
    assert.strictEqual(received.length, 1);
    assert.strictEqual(connection.handlerCount(AUTHORIZATION_EVENT_NAMES.resolved), 0);
  });

  it('12. keeps subscriptions and notifies onReconnected after a reconnect', async () => {
    const { client, connection } = buildClient();
    const received = [];
    let reconnects = 0;
    client.on(AUTHORIZATION_EVENT_NAMES.expired, (payload) => received.push(payload));
    client.onReconnected(() => {
      reconnects += 1;
    });

    await client.connect();
    connection.fireReconnected();

    assert.strictEqual(reconnects, 1);
    assert.strictEqual(connection.handlerCount(AUTHORIZATION_EVENT_NAMES.expired), 1, 'subscriptions survive reconnects');
    connection.emit(AUTHORIZATION_EVENT_NAMES.expired, { requestId: 7, status: 'Expired' });
    assert.strictEqual(received.length, 1);
  });

  it('13. exposes the three backend event names verbatim', () => {
    assert.deepStrictEqual(AUTHORIZATION_EVENT_NAMES, {
      requested: 'AuthorizationRequested',
      resolved: 'AuthorizationResolved',
      expired: 'AuthorizationExpired',
    });
  });
});