const CHANNEL_NAME = 'pos_tab_presence';

let responderChannel = null;
let probeInFlight = false;

export function isTabPresenceSupported() {
  return typeof window !== 'undefined' && typeof window.BroadcastChannel === 'function';
}

export function detectOtherTab(timeoutMs = 250) {
  if (!isTabPresenceSupported()) return Promise.resolve(false);

  return new Promise((resolve) => {
    let settled = false;
    let channel = null;
    let timer = null;

    const finish = (value) => {
      if (settled) return;
      settled = true;
      probeInFlight = false;
      if (timer) clearTimeout(timer);
      if (channel) {
        try {
          channel.close();
        } catch {}
      }
      resolve(value);
    };

    try {
      channel = new window.BroadcastChannel(CHANNEL_NAME);
    } catch {
      finish(false);
      return;
    }

    channel.onmessage = (event) => {
      if (event?.data?.type === 'pos-pong') {
        finish(true);
      }
    };

    probeInFlight = true;
    timer = setTimeout(() => finish(false), timeoutMs);

    try {
      channel.postMessage({ type: 'pos-ping' });
    } catch {
      finish(false);
    }
  });
}

export function startTabPresenceResponder() {
  if (!isTabPresenceSupported()) return () => {};

  if (!responderChannel) {
    try {
      responderChannel = new window.BroadcastChannel(CHANNEL_NAME);
      responderChannel.onmessage = (event) => {
        if (event?.data?.type !== 'pos-ping') return;
        if (probeInFlight) return;
        try {
          responderChannel?.postMessage({ type: 'pos-pong' });
        } catch {}
      };
    } catch {
      responderChannel = null;
      return () => {};
    }
  }

  let stopped = false;
  return () => {
    if (stopped) return;
    stopped = true;
    if (responderChannel) {
      try {
        responderChannel.close();
      } catch {}
    }
    responderChannel = null;
  };
}
