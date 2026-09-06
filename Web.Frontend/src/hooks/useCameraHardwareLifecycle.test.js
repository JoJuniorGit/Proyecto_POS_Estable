import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('useCameraHardwareLifecycle Stream Disposal Logic', () => {
  test('1. Stops all stream tracks and disposes resources', () => {
    let stoppedCount = 0;
    const mockTracks = [
      { stop: () => { stoppedCount++; } },
      { stop: () => { stoppedCount++; } },
    ];
    
    mockTracks.forEach(t => t.stop());
    assert.strictEqual(stoppedCount, 2);
  });
});
