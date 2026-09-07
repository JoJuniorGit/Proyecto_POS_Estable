import { describe, it } from 'node:test';
import assert from 'node:assert';
import { createCheckoutKeyHolder } from '../../utils/idempotency.js';

describe('CheckoutModal Idempotency-Key Lifecycle [8.2-CR1 / 8.6-B4]', () => {
  it('1. Generates a stable key for the current checkout attempt (retries share the SAME key)', () => {
    const holder = createCheckoutKeyHolder();
    const keyAttempt1 = holder.getOrCreateKey();
    assert.ok(keyAttempt1 && keyAttempt1.length > 0, 'a key must be generated lazily');

    // Network retry of the SAME attempt -> MUST retain exact same key!
    const keyRetry = holder.getOrCreateKey();
    assert.strictEqual(keyRetry, keyAttempt1, 'Retrying same attempt must preserve Idempotency-Key');
  });

  it('2. Resets the key on discard/completion so a fresh attempt gets a brand new key', () => {
    const holder = createCheckoutKeyHolder();
    const keyAttempt1 = holder.getOrCreateKey();

    holder.reset();
    const keyAfterReset = holder.getOrCreateKey();
    assert.notStrictEqual(keyAfterReset, null, 'a fresh key must be generated after reset');
    assert.notStrictEqual(keyAfterReset, keyAttempt1, 'Fresh attempt must generate a new Idempotency-Key');
  });

  it('3. Uses crypto.randomUUID when available (RFC 4122 collision-free keys)', () => {
    let called = 0;
    const holder = createCheckoutKeyHolder(() => {
      called++;
      return '00000000-0000-4000-8000-000000000000';
    });
    assert.strictEqual(holder.getOrCreateKey(), '00000000-0000-4000-8000-000000000000');
    assert.strictEqual(called, 1, 'crypto UUID must be generated only once per attempt');
    holder.reset();
    holder.getOrCreateKey();
    assert.strictEqual(called, 2, 'after reset a NEW uuid must be generated');
  });
});