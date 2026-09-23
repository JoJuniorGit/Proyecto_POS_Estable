import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert';
import {
  registerShutdownCleanup,
  runShutdownCleanups,
  shutdownCleanupCount,
  resetShutdownCleanups,
} from './shutdownRegistry.js';

describe('shutdownRegistry.js', () => {
  afterEach(() => {
    resetShutdownCleanups();
  });

  test('1. runShutdownCleanups ejecuta los handlers en orden inverso al registro (LIFO)', () => {
    const order = [];

    registerShutdownCleanup(() => order.push('first'));
    registerShutdownCleanup(() => order.push('second'));
    registerShutdownCleanup(() => order.push('third'));

    assert.strictEqual(shutdownCleanupCount(), 3);

    runShutdownCleanups();

    assert.deepStrictEqual(order, ['third', 'second', 'first']);
  });

  test('2. unregister evita la ejecución y el doble unregister es seguro', () => {
    const calls = [];
    const unregister = registerShutdownCleanup(() => calls.push('handler'));

    assert.strictEqual(shutdownCleanupCount(), 1);

    unregister();
    unregister();

    assert.strictEqual(shutdownCleanupCount(), 0);

    runShutdownCleanups();

    assert.deepStrictEqual(calls, []);

    const noop = registerShutdownCleanup('no-es-funcion');

    assert.strictEqual(typeof noop, 'function');
    assert.strictEqual(shutdownCleanupCount(), 0);
    assert.doesNotThrow(() => noop());
  });

  test('3. un handler que lanza no impide ejecutar los demás', () => {
    const calls = [];

    registerShutdownCleanup(() => calls.push('first'));
    registerShutdownCleanup(() => {
      throw new Error('boom');
    });
    registerShutdownCleanup(() => calls.push('third'));

    assert.doesNotThrow(() => runShutdownCleanups());
    assert.deepStrictEqual(calls, ['third', 'first']);
  });

  test('4. resetShutdownCleanups limpia el registro', () => {
    registerShutdownCleanup(() => {});
    registerShutdownCleanup(() => {});

    assert.strictEqual(shutdownCleanupCount(), 2);

    resetShutdownCleanups();
    assert.strictEqual(shutdownCleanupCount(), 0);

    let executed = false;
    registerShutdownCleanup(() => {
      executed = true;
    });
    resetShutdownCleanups();
    runShutdownCleanups();

    assert.strictEqual(executed, false);
  });
});
