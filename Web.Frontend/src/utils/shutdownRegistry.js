const shutdownHandlers = new Map();

let shutdownSequence = 0;

export function registerShutdownCleanup(handler) {
  if (typeof handler !== 'function') return () => {};

  const id = ++shutdownSequence;
  shutdownHandlers.set(id, handler);

  let released = false;
  return () => {
    if (released) return;
    released = true;
    shutdownHandlers.delete(id);
  };
}

export function runShutdownCleanups() {
  const handlers = Array.from(shutdownHandlers.values()).reverse();

  for (const handler of handlers) {
    try {
      handler();
    } catch {}
  }
}

export function shutdownCleanupCount() {
  return shutdownHandlers.size;
}

export function resetShutdownCleanups() {
  shutdownHandlers.clear();
}
