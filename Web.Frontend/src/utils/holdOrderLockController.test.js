import { describe, test } from 'node:test';
import assert from 'node:assert';
import { createHoldOrderLockController } from './holdOrderLockController.js';

function createSpies() {
  return {
    claimSale: async () => {},
    releaseSale: async () => {},
    reload: async () => {},
    onError: () => {},
  };
}

describe('holdOrderLockController [claim/release lifecycle]', () => {
  test('Start_ConPedidoLibreYAccionCheckout_ReclamaYDevuelveVerdadero', async () => {
    const spies = createSpies();
    let claimedId = null;
    let claimedAction = null;
    spies.claimSale = async (id, action) => { claimedId = id; claimedAction = action; };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 42, claimedByUserId: null };
    const result = await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(result, true);
    assert.strictEqual(claimedId, 42);
    assert.strictEqual(claimedAction, 'Checkout');
    assert.strictEqual(ctrl.getActiveLockId(), 42);
  });

  test('Start_ConAccionEditing_ReclamaConEditing', async () => {
    const spies = createSpies();
    let claimedAction = null;
    spies.claimSale = async (_id, action) => { claimedAction = action; };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 10, claimedByUserId: null };
    await ctrl.start(sale, 'Editing', 1);
    assert.strictEqual(claimedAction, 'Editing');
    assert.strictEqual(ctrl.getActiveLockId(), 10);
  });

  test('Start_ConBloqueoAjeno_NoReclamaYDevuelveFalso', async () => {
    const spies = createSpies();
    let claimCalled = false;
    spies.claimSale = async () => { claimCalled = true; };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 7, claimedByUserId: 99 };
    const result = await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(result, false);
    assert.strictEqual(claimCalled, false);
    assert.strictEqual(ctrl.getActiveLockId(), null);
  });

  test('Start_CuandoElReclamoFalla_RecargaYPropagaElMensaje', async () => {
    const spies = createSpies();
    let errorArg = null;
    spies.onError = (msg) => { errorArg = msg; };
    spies.claimSale = async () => { throw new Error('El pedido fue reclamado por otro'); };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 5, claimedByUserId: null };
    const result = await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(result, false);
    assert.strictEqual(errorArg, 'El pedido fue reclamado por otro');
    assert.strictEqual(ctrl.getActiveLockId(), null);
  });

  test('Start_CuandoFallaSinMensaje_UsaMensajePorDefecto', async () => {
    const spies = createSpies();
    let errorArg = null;
    spies.onError = (msg) => { errorArg = msg; };
    spies.claimSale = async () => { throw new Error(''); };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 5, claimedByUserId: null };
    await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(errorArg, 'No se pudo reclamar el pedido.');
  });

  test('Start_AlIniciar_LimpiaElError', async () => {
    const spies = createSpies();
    const calls = [];
    spies.onError = (msg) => { calls.push(msg); };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 5, claimedByUserId: null };
    await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(calls[0], null);
  });

  test('ReleaseActive_ConBloqueoActivo_LiberaYOlvidaElId', async () => {
    const spies = createSpies();
    let releasedId = null;
    spies.releaseSale = async (id) => { releasedId = id; };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 42, claimedByUserId: null };
    await ctrl.start(sale, 'Checkout', 1);
    await ctrl.releaseActive();
    assert.strictEqual(releasedId, 42);
    assert.strictEqual(ctrl.getActiveLockId(), null);
  });

  test('ReleaseActive_SinBloqueoActivo_NoLlamaRelease', async () => {
    const spies = createSpies();
    let releaseCalled = false;
    spies.releaseSale = async () => { releaseCalled = true; };
    const ctrl = createHoldOrderLockController(spies);
    await ctrl.releaseActive();
    assert.strictEqual(releaseCalled, false);
  });

  test('ReleaseActive_CuandoFalla_NoPropagaError', async () => {
    const spies = createSpies();
    let errorArg = 'initial';
    spies.onError = (msg) => { errorArg = msg; };
    spies.releaseSale = async () => { throw new Error('fail'); };
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 42, claimedByUserId: null };
    await ctrl.start(sale, 'Checkout', 1);
    errorArg = 'no-error-yet';
    await ctrl.releaseActive();
    assert.strictEqual(errorArg, 'no-error-yet');
  });

  test('ForceRelease_ConBloqueoAjeno_LiberaConForceYRecarga', async () => {
    const spies = createSpies();
    let releasedId = null;
    let releasedForce = null;
    let reloadCalled = false;
    spies.releaseSale = async (id, force) => { releasedId = id; releasedForce = force; };
    spies.reload = async () => { reloadCalled = true; };
    const ctrl = createHoldOrderLockController(spies);
    await ctrl.forceRelease({ id: 99 });
    assert.strictEqual(releasedId, 99);
    assert.strictEqual(releasedForce, true);
    assert.strictEqual(reloadCalled, true);
  });

  test('ForceRelease_CuandoFalla_PropagaElMensaje', async () => {
    const spies = createSpies();
    let errorArg = null;
    spies.onError = (msg) => { errorArg = msg; };
    spies.releaseSale = async () => { throw new Error('No se pudo liberar el pedido.'); };
    const ctrl = createHoldOrderLockController(spies);
    await ctrl.forceRelease({ id: 99 });
    assert.strictEqual(errorArg, 'No se pudo liberar el pedido.');
  });

  test('ForceRelease_DelPropioBloqueoActivo_OlvidaElId', async () => {
    const spies = createSpies();
    const ctrl = createHoldOrderLockController(spies);
    const sale = { id: 42, claimedByUserId: null };
    await ctrl.start(sale, 'Checkout', 1);
    assert.strictEqual(ctrl.getActiveLockId(), 42);
    await ctrl.forceRelease({ id: 42 });
    assert.strictEqual(ctrl.getActiveLockId(), null);
  });
});
