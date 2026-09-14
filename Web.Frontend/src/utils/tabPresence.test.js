import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert';
import { isTabPresenceSupported, detectOtherTab, startTabPresenceResponder } from './tabPresence.js';

class MockBroadcastChannel {
  static instances = [];

  constructor(name) {
    this.name = name;
    this.onmessage = null;
    this.closed = false;
    this.posted = [];
    MockBroadcastChannel.instances.push(this);
  }

  postMessage(data) {
    this.posted.push(data);
    for (const instance of MockBroadcastChannel.instances) {
      if (instance === this || instance.closed) continue;
      if (typeof instance.onmessage !== 'function') continue;
      instance.onmessage({ data });
    }
  }

  close() {
    this.closed = true;
  }

  static reset() {
    MockBroadcastChannel.instances = [];
  }
}

function setupWindow() {
  globalThis.window = { BroadcastChannel: MockBroadcastChannel };
}

describe('tabPresence.js', () => {
  afterEach(() => {
    delete globalThis.window;
    MockBroadcastChannel.reset();
  });

  test('a. sin soporte de BroadcastChannel detectOtherTab resuelve false', async () => {
    globalThis.window = {};

    assert.strictEqual(isTabPresenceSupported(), false);
    assert.strictEqual(await detectOtherTab(10), false);

    delete globalThis.window;
    assert.strictEqual(isTabPresenceSupported(), false);
    assert.strictEqual(await detectOtherTab(10), false);
  });

  test('b. detectOtherTab resuelve true ante un pos-pong inmediato', async () => {
    setupWindow();

    const pending = detectOtherTab(100);
    const detector = MockBroadcastChannel.instances[0];
    detector.onmessage({ data: { type: 'pos-pong' } });

    assert.strictEqual(await pending, true);
    assert.strictEqual(detector.closed, true);
  });

  test('c. detectOtherTab resuelve false cuando ninguna pestaña responde', async () => {
    setupWindow();

    assert.strictEqual(await detectOtherTab(10), false);
    assert.strictEqual(MockBroadcastChannel.instances[0].closed, true);
  });

  test('d. startTabPresenceResponder responde pos-pong a cada pos-ping', () => {
    setupWindow();

    const stop = startTabPresenceResponder();
    const probe = new MockBroadcastChannel('pos_tab_presence');
    const received = [];
    probe.onmessage = (event) => received.push(event.data);

    probe.postMessage({ type: 'pos-ping' });

    assert.deepStrictEqual(received, [{ type: 'pos-pong' }]);
    stop();
  });

  test('e. el responder no contesta el ping de su propia pestaña', async () => {
    setupWindow();

    const stop = startTabPresenceResponder();
    const result = await detectOtherTab(10);

    assert.strictEqual(result, false);
    stop();
  });
});
