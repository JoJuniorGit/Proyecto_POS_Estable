import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert';
import { checkBarcodeDetectorSupport, createNativeBarcodeDetector, SUPPORTED_1D_FORMATS } from './nativeBarcodeScanner.js';

describe('nativeBarcodeScanner.js Dual-Engine Detection', () => {
  let originalBarcodeDetector;

  beforeEach(() => {
    originalBarcodeDetector = global.BarcodeDetector;
  });

  afterEach(() => {
    global.BarcodeDetector = originalBarcodeDetector;
    if (typeof window !== 'undefined') {
      window.BarcodeDetector = originalBarcodeDetector;
    }
  });

  it('1. Returns true when BarcodeDetector supports essential 1D retail formats (ean_13 and code_128)', async () => {
    global.window = global.window || {};
    global.window.BarcodeDetector = class MockBarcodeDetector {
      static async getSupportedFormats() {
        return ['ean_13', 'ean_8', 'upc_a', 'code_128', 'code_39', 'qr_code'];
      }
    };

    const isSupported = await checkBarcodeDetectorSupport();
    assert.strictEqual(isSupported, true);
  });

  it('2. Returns false when BarcodeDetector is absent from window', async () => {
    global.window = global.window || {};
    delete global.window.BarcodeDetector;

    const isSupported = await checkBarcodeDetectorSupport();
    assert.strictEqual(isSupported, false);
  });

  it('3. Returns false in Safari iOS or restricted browsers that only support 2D/QR barcodes', async () => {
    global.window = global.window || {};
    global.window.BarcodeDetector = class MockSafariBarcodeDetector {
      static async getSupportedFormats() {
        return ['qr_code', 'data_matrix']; // No 1D formats
      }
    };

    const isSupported = await checkBarcodeDetectorSupport();
    assert.strictEqual(isSupported, false);
  });

  it('4. Returns false if getSupportedFormats throws an exception', async () => {
    global.window = global.window || {};
    global.window.BarcodeDetector = class ErrorBarcodeDetector {
      static async getSupportedFormats() {
        throw new Error('Not supported in iframe');
      }
    };

    const isSupported = await checkBarcodeDetectorSupport();
    assert.strictEqual(isSupported, false);
  });

  it('5. Instantiates BarcodeDetector with 1D formats when supported', () => {
    let passedOptions = null;
    global.window = global.window || {};
    global.window.BarcodeDetector = class MockBarcodeDetector {
      constructor(options) {
        passedOptions = options;
      }
    };

    const detector = createNativeBarcodeDetector();
    assert.ok(detector !== null);
    assert.deepStrictEqual(passedOptions, { formats: SUPPORTED_1D_FORMATS });
  });
});
