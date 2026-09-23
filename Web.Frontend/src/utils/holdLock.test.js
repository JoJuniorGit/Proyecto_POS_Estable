import { describe, test } from 'node:test';
import assert from 'node:assert';
import { CLAIM_ACTION_LABELS, isLockedByOther, getLockInfo } from './holdLock.js';

describe('getLockInfo [venta en espera multiterminal]', () => {
  test('getLockInfo_ConVentaSinReclamo_QuedaLibre', () => {
    const sale = { id: 1, claimedByUserId: null, claimedByUserName: null, claimAction: 'None' };
    assert.strictEqual(isLockedByOther(sale, 3), false);
    const info = getLockInfo(sale, 3);
    assert.strictEqual(info.isLocked, false);
    assert.strictEqual(info.isMine, false);
    assert.strictEqual(info.isLockedByOther, false);
    assert.strictEqual(info.label, null);
  });

  test('getLockInfo_ConVentaReclamadaPorOtroEnCheckout_IncluyeNombreYAccion', () => {
    const sale = { id: 2, claimedByUserId: 9, claimedByUserName: 'María Pérez', claimAction: 'Checkout' };
    assert.strictEqual(isLockedByOther(sale, 3), true);
    const info = getLockInfo(sale, 3);
    assert.strictEqual(info.isLocked, true);
    assert.strictEqual(info.isMine, false);
    assert.strictEqual(info.isLockedByOther, true);
    assert.strictEqual(info.label, 'Bloqueado por María Pérez - En proceso de pago');
  });

  test('getLockInfo_ConVentaReclamadaPorOtroEnEdicion_IncluyeEtiquetaEdicion', () => {
    const sale = { id: 3, claimedByUserId: 9, claimedByUserName: 'Juan', claimAction: 'Editing' };
    const info = getLockInfo(sale, 3);
    assert.strictEqual(info.label, 'Bloqueado por Juan - Editando pedido');
  });

  test('getLockInfo_ConVentaReclamadaPorMi_IndicaBloqueoPropio', () => {
    const sale = { id: 4, claimedByUserId: 3, claimedByUserName: 'Carlos', claimAction: 'Checkout' };
    assert.strictEqual(isLockedByOther(sale, 3), false);
    const info = getLockInfo(sale, 3);
    assert.strictEqual(info.isLocked, true);
    assert.strictEqual(info.isMine, true);
    assert.strictEqual(info.isLockedByOther, false);
    assert.strictEqual(info.label, 'Bloqueado por ti');
  });

  test('getLockInfo_SinNombreDeCajero_UsaFallbackOtroCajero', () => {
    const sale = { id: 5, claimedByUserId: 9, claimedByUserName: null, claimAction: 'Checkout' };
    const info = getLockInfo(sale, 3);
    assert.strictEqual(info.label, 'Bloqueado por otro cajero - En proceso de pago');
  });

  test('getLockInfo_ConAccionDesconocida_UsaFallbackEnProceso', () => {
    const sale = { id: 6, claimedByUserId: 9, claimedByUserName: 'Ana', claimAction: 'None' };
    assert.strictEqual(getLockInfo(sale, 3).label, 'Bloqueado por Ana - en proceso');
    assert.strictEqual(getLockInfo({ ...sale, claimAction: 'OtraCosa' }, 3).label, 'Bloqueado por Ana - en proceso');
    assert.strictEqual(getLockInfo({ ...sale, claimAction: undefined }, 3).label, 'Bloqueado por Ana - en proceso');
  });
});

describe('isLockedByOther [venta en espera multiterminal]', () => {
  test('isLockedByOther_ConVentaNula_EsFalso', () => {
    assert.strictEqual(isLockedByOther(null, 3), false);
    assert.strictEqual(isLockedByOther(undefined, 3), false);
  });

  test('isLockedByOther_SinUsuarioActualYVentaReclamada_EsVerdadero', () => {
    assert.strictEqual(isLockedByOther({ claimedByUserId: 9 }, undefined), true);
  });

  test('isLockedByOther_ConReclamoPropio_EsFalso', () => {
    assert.strictEqual(isLockedByOther({ claimedByUserId: 3 }, 3), false);
  });

  test('CLAIM_ACTION_LABELS_ExponeEtiquetasEnEspanol', () => {
    assert.deepStrictEqual(CLAIM_ACTION_LABELS, {
      Checkout: 'En proceso de pago',
      Editing: 'Editando pedido',
    });
  });
});
