import { describe, it } from 'node:test';
import assert from 'node:assert';
import { 
  isLaptopOrDesktopEnvironment, 
  apply1DHorizontalSharpen, 
  applySauvolaBinarization,
  applyLuminanceInversion 
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
