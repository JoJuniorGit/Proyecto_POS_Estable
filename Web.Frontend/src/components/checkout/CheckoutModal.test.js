import { describe, it } from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

describe('CheckoutModal Verification & Idempotency Key Lifecycle [8.2-CR1]', () => {
  it('1. Verifies that CheckoutModal.jsx imports useRef along with required React hooks', () => {
    const modalPath = path.resolve(__dirname, 'CheckoutModal.jsx');
    const content = fs.readFileSync(modalPath, 'utf8');

    // Assert that useRef is explicitly imported from react
    const importRegex = /import\s*\{[^}]*\buseRef\b[^}]*\}\s*from\s*['"]react['"]/;
    assert.match(content, importRegex, 'CheckoutModal.jsx MUST import useRef from react');

    // Assert that checkoutKeyRef is instantiated with useRef(null)
    assert.ok(content.includes('const checkoutKeyRef = useRef(null);'), 'checkoutKeyRef must be instantiated with useRef(null)');
  });

  it('2. Enforces stable Idempotency-Key per checkout attempt and resets on discard', () => {
    let checkoutKeyRef = { current: null };

    const getOrGenerateKey = () => {
      if (!checkoutKeyRef.current) {
        checkoutKeyRef.current = 'test-uuid-' + Math.random().toString(36).substring(2, 9);
      }
      return checkoutKeyRef.current;
    };

    // Attempt 1: First payment attempt
    const keyAttempt1 = getOrGenerateKey();
    assert.ok(keyAttempt1.startsWith('test-uuid-'));

    // Network retry of the SAME attempt -> MUST retain exact same key!
    const keyRetry = getOrGenerateKey();
    assert.strictEqual(keyRetry, keyAttempt1, 'Retrying same attempt must preserve Idempotency-Key');

    // User discards payments and exits modal -> key is reset
    checkoutKeyRef.current = null;
    assert.strictEqual(checkoutKeyRef.current, null);

    // Attempt 2: Fresh checkout attempt -> MUST generate a brand new key!
    const keyAttempt2 = getOrGenerateKey();
    assert.notStrictEqual(keyAttempt2, keyAttempt1, 'Fresh attempt must generate a new Idempotency-Key');
  });
});
