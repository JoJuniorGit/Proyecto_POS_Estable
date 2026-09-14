import { test, describe, afterEach } from 'node:test';
import assert from 'node:assert';
import { registerOpenModal, hasOpenModals, resetOpenModals } from './modalRegistry.js';

describe('modalRegistry.js', () => {
  afterEach(() => {
    resetOpenModals();
  });

  test('1. registros anidados mantienen el conteo y liberan de forma independiente', () => {
    assert.strictEqual(hasOpenModals(), false);

    const closeOuter = registerOpenModal();
    const closeInner = registerOpenModal();

    assert.strictEqual(hasOpenModals(), true);

    closeInner();
    assert.strictEqual(hasOpenModals(), true);

    closeOuter();
    assert.strictEqual(hasOpenModals(), false);
  });

  test('2. doble release es seguro y no decrementa dos veces', () => {
    const closeInner = registerOpenModal();
    const closeOuter = registerOpenModal();

    closeInner();
    closeInner();
    assert.strictEqual(hasOpenModals(), true);

    closeOuter();
    assert.strictEqual(hasOpenModals(), false);
  });

  test('3. resetOpenModals limpia el conteo', () => {
    registerOpenModal();
    registerOpenModal();
    assert.strictEqual(hasOpenModals(), true);

    resetOpenModals();
    assert.strictEqual(hasOpenModals(), false);
  });
});
