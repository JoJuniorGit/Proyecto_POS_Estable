export const THEME_STORAGE_KEY = 'pos-theme';
export const THEME_SYNC_CHANNEL = 'pos_theme_sync';
export const THEME_VALUES = ['light', 'dark'];

export const CURRENCY_FORMAT_STORAGE_KEY = 'currency_format_preference';
export const CURRENCY_FORMAT_SYNC_CHANNEL = 'pos_currency_format_sync';
export const CURRENCY_FORMAT_VALUES = ['Venezuelan', 'International'];

const SYNC_MESSAGE_TYPE = 'pos-state-sync';

function getStorage() {
  try {
    if (typeof window !== 'undefined' && window.localStorage) return window.localStorage;
    if (typeof localStorage !== 'undefined') return localStorage;
  } catch {
    return null;
  }
  return null;
}

export function isCrossTabSyncSupported() {
  return typeof window !== 'undefined' && typeof window.BroadcastChannel === 'function';
}

export function resolveStateSyncMessage(message, allowedValues) {
  if (message?.type !== SYNC_MESSAGE_TYPE) return null;
  return allowedValues.includes(message.value) ? message.value : null;
}

export function resolveRevalidatedValue(storedValue, currentValue, allowedValues) {
  if (!allowedValues.includes(storedValue) || storedValue === currentValue) return currentValue;
  return storedValue;
}

export function readStoredState(storageKey, allowedValues) {
  const storage = getStorage();
  if (!storage) return null;
  try {
    const value = storage.getItem(storageKey);
    return allowedValues.includes(value) ? value : null;
  } catch {
    return null;
  }
}

export function writeStoredState(storageKey, value) {
  const storage = getStorage();
  if (!storage) return;
  try {
    storage.setItem(storageKey, value);
  } catch {}
}

export function broadcastStateSync(channelName, value) {
  if (!isCrossTabSyncSupported()) return;

  let channel = null;
  try {
    channel = new window.BroadcastChannel(channelName);
    channel.postMessage({ type: SYNC_MESSAGE_TYPE, value });
  } catch {
    return;
  } finally {
    try {
      channel?.close();
    } catch {}
  }
}

export function subscribeStateSync(channelName, allowedValues, onValue) {
  if (!isCrossTabSyncSupported()) return () => {};

  let channel = null;
  try {
    channel = new window.BroadcastChannel(channelName);
  } catch {
    return () => {};
  }

  channel.onmessage = (event) => {
    const value = resolveStateSyncMessage(event?.data, allowedValues);
    if (value !== null) onValue(value);
  };

  return () => {
    try {
      channel.close();
    } catch {}
  };
}

export function subscribeWindowRevalidation(onRevalidate) {
  if (typeof window === 'undefined' || typeof document === 'undefined') return () => {};

  const handleFocus = () => onRevalidate();
  const handleVisibilityChange = () => {
    if (document.visibilityState === 'visible') onRevalidate();
  };

  window.addEventListener('focus', handleFocus);
  document.addEventListener('visibilitychange', handleVisibilityChange);
  return () => {
    window.removeEventListener('focus', handleFocus);
    document.removeEventListener('visibilitychange', handleVisibilityChange);
  };
}
