import { describe, it } from 'node:test';
import assert from 'node:assert';
import { selectEffectiveRate } from './CartContext.jsx';

describe('selectEffectiveRate decision table', () => {
  it('1. Prefers the live global rate when it is greater than zero', () => {
    assert.strictEqual(selectEffectiveRate(52.4, 50), 52.4);
  });

  it('2. Falls back to the sale applied rate when the live rate is zero or absent', () => {
    assert.strictEqual(selectEffectiveRate(0, 50), 50);
    assert.strictEqual(selectEffectiveRate(undefined, 50), 50);
    assert.strictEqual(selectEffectiveRate(null, 50), 50);
  });

  it('3. Uses the live rate when the sale has no applied rate', () => {
    assert.strictEqual(selectEffectiveRate(52.4, undefined), 52.4);
    assert.strictEqual(selectEffectiveRate(52.4, 0), 52.4);
  });

  it('4. Defaults to 1 when both rates are zero or absent', () => {
    assert.strictEqual(selectEffectiveRate(0, 0), 1);
    assert.strictEqual(selectEffectiveRate(0, undefined), 1);
    assert.strictEqual(selectEffectiveRate(undefined, undefined), 1);
  });

  it('5. Ignores negative live rates and keeps the sale snapshot', () => {
    assert.strictEqual(selectEffectiveRate(-1, 50), 50);
  });

  it('6. Is deterministic when both rates are equal', () => {
    assert.strictEqual(selectEffectiveRate(52.4, 52.4), 52.4);
  });
});
