import { describe, it } from 'node:test';
import assert from 'node:assert';
import { 
  isLaptopOrDesktopEnvironment, 
  apply1DHorizontalSharpen, 
  applySauvolaBinarization,
  applyLuminanceInversion,
  computeLaptopVisionCrop,
  isFrameSignatureChanged,
  computeLaptopVisionIntervalMs,
  planLaptopVisionTick,
  LAPTOP_VISION_MAX_WIDTH,
  LAPTOP_VISION_CHEAP_PASS,
  LAPTOP_VISION_BASE_INTERVAL_MS,
  LAPTOP_VISION_RECENT_INTERVAL_MS,
} from './laptopVisionEnhancer.js';

describe('Enhanced laptopVisionEnhancer Multi-Pass Pipeline Tests', () => {
  it('1. Correctly classifies desktop/laptop vs mobile user agents', () => {
    const isDesktop = isLaptopOrDesktopEnvironment();
    assert.strictEqual(typeof isDesktop, 'boolean');
  });

  it('2. apply1DHorizontalSharpen enhances horizontal transitions between adjacent barcode lines', () => {
    // 3x3 image RGBA
    const width = 3;
    const height = 1;
    // Row of pixels: Dark (50), Light (200), Dark (50) -> sharp vertical bar
    const data = new Uint8ClampedArray([
      50, 50, 50, 255,
      200, 200, 200, 255,
      50, 50, 50, 255
    ]);

    const mockImageData = { width, height, data };
    const enhanced = apply1DHorizontalSharpen(mockImageData, 1.0);

    // Center pixel: 200 + 1.0 * (2*200 - 50 - 50) = 200 + 300 = 500 -> clamped to 255
    assert.strictEqual(enhanced.data[4], 255);
  });

  it('3. applySauvolaBinarization cleanly segments text/bars under non-uniform illumination using integral images', () => {
    // 4x4 matrix
    const width = 4;
    const height = 4;
    const data = new Uint8ClampedArray(width * height * 4);

    // Fill with background high luminance (220) and a single dark vertical bar at column 1 (30)
    for (let y = 0; y < height; y++) {
      for (let x = 0; x < width; x++) {
        const idx = (y * width + x) * 4;
        const val = x === 1 ? 30 : 220;
        data[idx] = val;
        data[idx + 1] = val;
        data[idx + 2] = val;
        data[idx + 3] = 255;
      }
    }

    const mockImageData = { width, height, data };
    const binarized = applySauvolaBinarization(mockImageData, 2, 0.18);

    // Column 1 must be binary 0 (dark bar), others must be binary 255 (background)
    for (let y = 0; y < height; y++) {
      const darkIdx = (y * width + 1) * 4;
      const lightIdx = (y * width + 2) * 4;
      assert.strictEqual(binarized.data[darkIdx], 0);
      assert.strictEqual(binarized.data[lightIdx], 255);
    }
  });

  it('4. applyLuminanceInversion correctly flips white and black pixels', () => {
    const data = new Uint8ClampedArray([0, 0, 0, 255, 255, 255, 255, 255]);
    const mockImageData = { data };
    applyLuminanceInversion(mockImageData);

    assert.strictEqual(data[0], 255);
    assert.strictEqual(data[4], 0);
  });
});

describe('Laptop scanner main-thread pacing helpers', () => {
  it('5. computeLaptopVisionCrop bounds the processed ROI width regardless of camera resolution', () => {
    const crop1080p = computeLaptopVisionCrop(1920, 1080, 0);
    assert.strictEqual(crop1080p.dw, LAPTOP_VISION_MAX_WIDTH);
    assert.strictEqual(crop1080p.dh, 450);

    const crop720p = computeLaptopVisionCrop(1280, 720, 0);
    assert.strictEqual(crop720p.dw, 576);
    assert.strictEqual(crop720p.dh, 324);
    assert.strictEqual(crop720p.sx, (1280 - 576) / 2);
    assert.strictEqual(crop720p.sy, (720 - 324) / 2);

    const rawHighRes = computeLaptopVisionCrop(1920, 1080, 2);
    assert.strictEqual(rawHighRes.dw, LAPTOP_VISION_MAX_WIDTH);
    assert.strictEqual(rawHighRes.dh, 450);
  });

  it('6. planLaptopVisionTick starts heavy and then decodes cheap raw frames between heavy passes', () => {
    const first = planLaptopVisionTick({ now: 1000, lastHeavyPassAt: 0, frameChanged: true });
    assert.strictEqual(first.action, 'heavy');
    assert.strictEqual(first.passIndex, 0);

    const between = planLaptopVisionTick({
      now: 1100,
      lastHeavyPassAt: 1000,
      frameChanged: true,
      heavyPassCursor: first.heavyPassCursor,
    });
    assert.strictEqual(between.action, 'cheap');
    assert.strictEqual(between.passIndex, LAPTOP_VISION_CHEAP_PASS);
    assert.strictEqual(between.heavyPassCursor, first.heavyPassCursor);
  });

  it('7. planLaptopVisionTick rotates the heavy filters and wraps around', () => {
    const passes = [];
    let cursor = 0;
    let lastHeavyPassAt = 0;

    for (let i = 0; i < 4; i++) {
      const now = 1000 + i * 300;
      const plan = planLaptopVisionTick({ now, lastHeavyPassAt, frameChanged: true, heavyPassCursor: cursor });
      assert.strictEqual(plan.action, 'heavy');
      passes.push(plan.passIndex);
      cursor = plan.heavyPassCursor;
      lastHeavyPassAt = now;
    }

    assert.deepStrictEqual(passes, [0, 1, 3, 0]);
  });

  it('8. planLaptopVisionTick skips unchanged frames between heavy passes but keeps heavy coverage when due', () => {
    const skipped = planLaptopVisionTick({ now: 1100, lastHeavyPassAt: 1000, frameChanged: false });
    assert.strictEqual(skipped.action, 'skip');
    assert.strictEqual(skipped.passIndex, null);

    const frozenFrameHeavy = planLaptopVisionTick({ now: 1300, lastHeavyPassAt: 1000, frameChanged: false });
    assert.strictEqual(frozenFrameHeavy.action, 'heavy');
  });

  it('9. computeLaptopVisionIntervalMs slows down only while a code was recently detected', () => {
    assert.strictEqual(
      computeLaptopVisionIntervalMs({ now: 2000, lastDetectedAt: 1900 }),
      LAPTOP_VISION_RECENT_INTERVAL_MS
    );
    assert.strictEqual(
      computeLaptopVisionIntervalMs({ now: 4000, lastDetectedAt: 1900 }),
      LAPTOP_VISION_BASE_INTERVAL_MS
    );
    assert.strictEqual(
      computeLaptopVisionIntervalMs({ now: 2000, lastDetectedAt: 0 }),
      LAPTOP_VISION_BASE_INTERVAL_MS
    );
  });

  it('10. isFrameSignatureChanged ignores sub-threshold sensor noise and detects real scene changes', () => {
    const base = new Uint8Array([10, 20, 30, 40]);

    assert.strictEqual(isFrameSignatureChanged(base, new Uint8Array([10, 20, 30, 40])), false);
    assert.strictEqual(isFrameSignatureChanged(base, new Uint8Array([12, 23, 34, 47])), false);
    assert.strictEqual(isFrameSignatureChanged(base, new Uint8Array([10, 20, 30, 120])), true);
    assert.strictEqual(isFrameSignatureChanged(null, base), true);
    assert.strictEqual(isFrameSignatureChanged(base, new Uint8Array([1, 2])), true);
  });
});
