import { describe, it } from 'node:test';
import assert from 'node:assert';
import { isValidBarcode, validateGs1Mod10Checksum } from './barcodeValidator.js';

describe('barcodeValidator.js isValidBarcode & validateGs1Mod10Checksum', () => {
  it('1. Accepts valid standard 1D barcodes and valid GS1 Mod10 checksums', () => {
    // Valid EAN-13
    assert.strictEqual(isValidBarcode('7591001002009'), true);
    assert.strictEqual(isValidBarcode('5901234123457'), true);
    
    // Valid UPC-A
    assert.strictEqual(isValidBarcode('036000291452'), true);
    
    // Valid EAN-8
    assert.strictEqual(isValidBarcode('75910013'), true);
    
    // Valid GS1 In-store scale barcode (prefix 20-29)
    assert.strictEqual(isValidBarcode('2012345005006'), true);

    // Valid alphanumeric and internal SKUs
    assert.strictEqual(isValidBarcode('SKU123456'), true);
    assert.strictEqual(isValidBarcode('PROD001'), true);
    assert.strictEqual(isValidBarcode('1001'), true);
    assert.strictEqual(isValidBarcode('123456789012345'), true); // 15 chars
  });

  it('2. Rejects corrupted GS1 barcodes with invalid Mod10 checksum', () => {
    // Corrupted EAN-13 (last digit changed)
    assert.strictEqual(isValidBarcode('7591001002003'), false);
    assert.strictEqual(isValidBarcode('5901234123450'), false);
    
    // Corrupted UPC-A
    assert.strictEqual(isValidBarcode('036000291459'), false);
    
    // Corrupted EAN-8
    assert.strictEqual(isValidBarcode('75910019'), false);

    // Corrupted scale barcode
    assert.strictEqual(isValidBarcode('2012345005009'), false);
  });

  it('3. Rejects QR URLs and HTTP/HTTPS links', () => {
    assert.strictEqual(isValidBarcode('http://example.com/item?id=10'), false);
    assert.strictEqual(isValidBarcode('https://menu.pos.com/qr'), false);
    assert.strictEqual(isValidBarcode('http://192.168.1.5:5000'), false);
    assert.strictEqual(isValidBarcode('https://my-store.ve/product/123'), false);
  });

  it('4. Rejects special characters (?, =, &, slashes, spaces, colons)', () => {
    assert.strictEqual(isValidBarcode('item?id=10'), false);
    assert.strictEqual(isValidBarcode('query=1&param=2'), false);
    assert.strictEqual(isValidBarcode('PROD 123'), false);
    assert.strictEqual(isValidBarcode('SKU-1001'), false);
    assert.strictEqual(isValidBarcode('CODE#123'), false);
    assert.strictEqual(isValidBarcode('item/123'), false);
  });

  it('5. Rejects out of range lengths (< 4 or > 15 chars) and empty values', () => {
    assert.strictEqual(isValidBarcode('123'), false); // 3 chars
    assert.strictEqual(isValidBarcode('1234567890123456'), false); // 16 chars
    assert.strictEqual(isValidBarcode(''), false);
    assert.strictEqual(isValidBarcode('   '), false);
    assert.strictEqual(isValidBarcode(null), false);
    assert.strictEqual(isValidBarcode(undefined), false);
  });
});
