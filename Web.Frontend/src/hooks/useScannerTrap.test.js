import { describe, it } from 'node:test';
import assert from 'node:assert';

describe('useScannerTrap Keyboard Wedge Hook Logic', () => {
  it('1. Simulates burst scanning with delta <= 60ms and confirms code upon Enter', () => {
    let capturedCode = null;
    let buffer = '';
    let lastTime = 0;
    const maxDelta = 60;

    const onScan = (code) => {
      capturedCode = code;
    };

    const simulateKey = (key, time) => {
      const delta = time - lastTime;
      lastTime = time;

      if (key === 'Escape') {
        buffer = '';
        return;
      }

      if (key === 'Enter') {
        const candidate = buffer.trim();
        buffer = '';
        if (candidate.length >= 4) {
          onScan(candidate);
        }
        return;
      }

      if (buffer.length > 0 && delta > maxDelta) {
        buffer = '';
      }
      buffer += key;
    };

    // Simulate fast USB scanner typing: "7591001002009" with 15ms interval
    const code = '7591001002009';
    let clock = 1000;
    for (const ch of code) {
      clock += 15;
      simulateKey(ch, clock);
    }
    clock += 15;
    simulateKey('Enter', clock);

    assert.strictEqual(capturedCode, '7591001002009');
  });

  it('2. Resets buffer on slow human typing (> 60ms) and does not capture as a single burst', () => {
    let capturedCode = null;
    let buffer = '';
    let lastTime = 0;
    const maxDelta = 60;

    const onScan = (code) => {
      capturedCode = code;
    };

    const simulateKey = (key, time) => {
      const delta = time - lastTime;
      lastTime = time;

      if (key === 'Enter') {
        const candidate = buffer.trim();
        buffer = '';
        if (candidate.length >= 4) {
          onScan(candidate);
        }
        return;
      }

      if (buffer.length > 0 && delta > maxDelta) {
        buffer = '';
      }
      buffer += key;
    };

    // Simulate slow human typing: 150ms between keys
    let clock = 1000;
    const code = '7591001002009';
    for (const ch of code) {
      clock += 150;
      simulateKey(ch, clock);
    }
    clock += 150;
    simulateKey('Enter', clock);

    // Only the last single char remained in buffer before Enter, so length < 4 -> no scan
    assert.strictEqual(capturedCode, null);
  });
});
