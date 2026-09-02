/**
 * Módulo de síntesis de audio y respuesta háptica para escáner POS.
 * Utiliza Web Audio API con osciladores senoidales puros en memoria (cero dependencias externas).
 * Incluye gestión de políticas de autoplay (lazy creation y verificación de estado suspended/resume).
 */

let audioCtx = null;

function getAudioContext() {
  if (typeof window === 'undefined') return null;
  const AudioContextClass = window.AudioContext || window.webkitAudioContext;
  if (!AudioContextClass) return null;

  if (!audioCtx) {
    try {
      audioCtx = new AudioContextClass();
    } catch {
      audioCtx = null;
    }
  }

  return audioCtx;
}

async function ensureAudioContextActive(ctx) {
  if (!ctx) return false;
  if (ctx.state === 'suspended') {
    try {
      await ctx.resume();
    } catch {
      return false;
    }
  }
  return ctx.state === 'running';
}

function playTone(frequency, durationMs, gainLevel = 0.2) {
  try {
    const ctx = getAudioContext();
    if (!ctx) return;

    void ensureAudioContextActive(ctx).then((active) => {
      if (!active) return;

      const osc = ctx.createOscillator();
      const gain = ctx.createGain();

      const now = ctx.currentTime;
      const durationSec = durationMs / 1000;

      osc.type = 'sine';
      osc.frequency.setValueAtTime(frequency, now);

      // Curva suave de ataque y decaimiento exponencial para evitar chasquidos acústicos
      gain.gain.setValueAtTime(0.001, now);
      gain.gain.exponentialRampToValueAtTime(gainLevel, now + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, now + durationSec);

      osc.connect(gain);
      gain.connect(ctx.destination);

      osc.start(now);
      osc.stop(now + durationSec + 0.05);
    });
  } catch {
    // Manejo seguro: un fallo de audio no debe interrumpir la venta
  }
}

/**
 * Tono agudo y limpio (880 Hz) indicando producto reconocido y agregado a la venta.
 */
export function playScanSuccess() {
  playTone(880, 80, 0.25);
  triggerHapticFeedback([60]);
}

/**
 * Tono medio de advertencia (440 Hz) indicando producto no registrado.
 */
export function playScanWarning() {
  playTone(440, 120, 0.3);
  triggerHapticFeedback([40, 40, 40]);
}

/**
 * Tono grave de error (220 Hz) indicando producto inactivo o error de red.
 */
export function playScanError() {
  playTone(220, 180, 0.35);
  triggerHapticFeedback([120]);
}

/**
 * Dispara vibración en dispositivos móviles si está soportado.
 * @param {number[]} pattern
 */
export function triggerHapticFeedback(pattern = [60]) {
  try {
    if (typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function') {
      navigator.vibrate(pattern);
    }
  } catch {
    // Ignorar si el navegador bloquea vibraciones
  }
}
