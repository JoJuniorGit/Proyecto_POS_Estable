const SNAPSHOT_KEY = 'pos_orphan_sale_snapshot';
const CLEAN_SHUTDOWN_KEY = 'pos_clean_shutdown_at';
const ACTIVE_SALE_ID_KEY = 'active_pos_sale_id';

export const RECOVERY_MAX_AGE_MS = 24 * 60 * 60 * 1000;

function getLocalStorage() {
  try {
    if (typeof window === 'undefined' || !window.localStorage) return null;
    return window.localStorage;
  } catch {
    return null;
  }
}

function getSessionStorage() {
  try {
    if (typeof window === 'undefined' || !window.sessionStorage) return null;
    return window.sessionStorage;
  } catch {
    return null;
  }
}

function readStorageValue(storage, key) {
  if (!storage) return null;

  try {
    return storage.getItem(key);
  } catch {
    return null;
  }
}

export function readRecoverySnapshot() {
  const raw = readStorageValue(getLocalStorage(), SNAPSHOT_KEY);
  if (!raw) return null;

  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    return parsed;
  } catch {
    return null;
  }
}

export function readCleanShutdown() {
  const raw = readStorageValue(getLocalStorage(), CLEAN_SHUTDOWN_KEY);
  if (!raw) return null;

  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    return parsed;
  } catch {
    return null;
  }
}

export function canOfferRecovery(snapshot, now = Date.now()) {
  if (!snapshot || typeof snapshot !== 'object') return false;

  const saleId = Number(snapshot.saleId);
  if (!Number.isFinite(saleId) || saleId <= 0) return false;

  const itemCount = Number(snapshot.itemCount);
  if (!Number.isFinite(itemCount) || itemCount <= 0) return false;

  if (snapshot.status !== 'Pending') return false;

  const savedAt = Number(snapshot.savedAt);
  if (!Number.isFinite(savedAt) || savedAt <= 0) return false;

  return now - savedAt <= RECOVERY_MAX_AGE_MS;
}

export function hasOrphanedSnapshot(now = Date.now()) {
  const snapshot = readRecoverySnapshot();
  if (!canOfferRecovery(snapshot, now)) return false;

  const clean = readCleanShutdown();
  if (clean && clean.saleId === Number(snapshot.saleId) && clean.at >= Number(snapshot.savedAt)) return false;

  const activeSaleId = readStorageValue(getSessionStorage(), ACTIVE_SALE_ID_KEY);
  if (activeSaleId === String(snapshot.saleId)) return false;

  return true;
}

export function saveRecoverySnapshot(snapshot) {
  const storage = getLocalStorage();
  if (!storage || !snapshot?.saleId) return;

  try {
    storage.setItem(
      SNAPSHOT_KEY,
      JSON.stringify({
        version: 1,
        saleId: Number(snapshot.saleId),
        cashierId: snapshot.cashierId ?? null,
        cashierName: snapshot.cashierName ?? null,
        customerName: snapshot.customerName ?? null,
        itemCount: Number(snapshot.itemCount || 0),
        totalUSD: Number(snapshot.totalUSD || 0),
        status: snapshot.status || 'Pending',
        savedAt: Date.now(),
      })
    );
  } catch {}
}

export function clearRecoverySnapshot() {
  const storage = getLocalStorage();
  if (!storage) return;

  try {
    storage.removeItem(SNAPSHOT_KEY);
  } catch {}
}

export function markCleanShutdown(saleId, now = Date.now()) {
  const storage = getLocalStorage();
  if (!storage) return;

  try {
    storage.setItem(CLEAN_SHUTDOWN_KEY, JSON.stringify({ saleId: Number(saleId) || null, at: now }));
  } catch {}
}

export function markSessionDirty() {
  const storage = getLocalStorage();
  if (!storage) return;

  try {
    storage.removeItem(CLEAN_SHUTDOWN_KEY);
  } catch {}
}
