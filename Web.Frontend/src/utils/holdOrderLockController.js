import { isLockedByOther } from './holdLock.js';

export function createHoldOrderLockController({ claimSale, releaseSale, reload, onError }) {
  let activeLockId = null;

  return {
    async start(sale, action, currentUserId) {
      if (isLockedByOther(sale, currentUserId)) return false;
      onError(null);
      try {
        await claimSale(sale.id, action);
        activeLockId = sale.id;
        return true;
      } catch (err) {
        await reload();
        onError(err.message || 'No se pudo reclamar el pedido.');
        return false;
      }
    },

    async releaseActive() {
      if (activeLockId === null || activeLockId === undefined) return;
      try {
        await releaseSale(activeLockId);
        activeLockId = null;
      } catch {
        return;
      }
    },

    async forceRelease(sale) {
      onError(null);
      try {
        await releaseSale(sale.id, true);
        if (activeLockId === sale.id) activeLockId = null;
        await reload();
      } catch (err) {
        onError(err.message || 'No se pudo liberar el pedido.');
      }
    },

    getActiveLockId() {
      return activeLockId;
    },
  };
}
