import { describe, it } from 'node:test';
import assert from 'node:assert';
import { playScanSuccess, playScanWarning, playScanError, triggerHapticFeedback } from './soundEffects.js';

describe('soundEffects.js', () => {
  it('1. Calling audio and haptic functions does not throw when AudioContext/vibrate are unavailable', () => {
    assert.doesNotThrow(() => playScanSuccess());
    assert.doesNotThrow(() => playScanWarning());
    assert.doesNotThrow(() => playScanError());
    assert.doesNotThrow(() => triggerHapticFeedback([50]));
  });
});
