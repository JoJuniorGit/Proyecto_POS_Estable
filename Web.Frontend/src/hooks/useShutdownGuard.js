import { useEffect, useRef } from 'react';
import { runShutdownCleanups } from '../utils/shutdownRegistry';
import { markCleanShutdown, markSessionDirty, readRecoverySnapshot } from '../utils/saleRecovery';

export const UNLOAD_WARNING_MESSAGE = '¿Seguro que desea abandonar? Hay una venta en curso o ventanas sin guardar.';

export function handleBeforeUnloadEvent(event, hasVolatileState) {
  if (!hasVolatileState) return undefined;

  event.preventDefault();
  event.returnValue = UNLOAD_WARNING_MESSAGE;
  return UNLOAD_WARNING_MESSAGE;
}

export function handleShutdownSequence({ flushState } = {}) {
  try {
    flushState?.();
  } catch {}

  const snapshot = readRecoverySnapshot();
  if (snapshot?.saleId) {
    markCleanShutdown(snapshot.saleId);
  }

  runShutdownCleanups();
}

export function shouldRunShutdownOnPageHide(event) {
  return !event?.persisted;
}

export function handleBfcacheRestore() {
  markSessionDirty();
}

export function useShutdownGuard({ getHasVolatileState, flushState } = {}) {
  const getHasVolatileStateRef = useRef(getHasVolatileState);
  const flushStateRef = useRef(flushState);

  getHasVolatileStateRef.current = getHasVolatileState;
  flushStateRef.current = flushState;

  useEffect(() => {
    if (typeof window === 'undefined') return undefined;

    const handleBeforeUnload = (event) => {
      handleBeforeUnloadEvent(event, getHasVolatileStateRef.current?.());
    };

    const handlePageHide = (event) => {
      if (!shouldRunShutdownOnPageHide(event)) return;
      handleShutdownSequence({ flushState: flushStateRef.current });
    };

    const handlePageShow = (event) => {
      if (event?.persisted) {
        handleBfcacheRestore();
      }
    };

    const handleVisibilityChange = () => {
      if (typeof document === 'undefined' || document.visibilityState !== 'hidden') return;

      try {
        flushStateRef.current?.();
      } catch {}
    };

    const hasDocument = typeof document !== 'undefined';

    window.addEventListener('beforeunload', handleBeforeUnload);
    window.addEventListener('pagehide', handlePageHide);
    window.addEventListener('pageshow', handlePageShow);
    if (hasDocument) {
      document.addEventListener('visibilitychange', handleVisibilityChange);
    }

    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
      window.removeEventListener('pagehide', handlePageHide);
      window.removeEventListener('pageshow', handlePageShow);
      if (hasDocument) {
        document.removeEventListener('visibilitychange', handleVisibilityChange);
      }
    };
  }, []);
}
