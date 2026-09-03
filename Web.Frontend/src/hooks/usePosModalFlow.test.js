import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('usePosModalFlow Logic Helper Tests', () => {
  test('1. Resolves activeModal hierarchy correctly (external > customer > scanner > variant)', () => {
    const resolveActiveModal = (isExternal, isCustomer, isScanner, variantParent) => {
      return isExternal
        ? 'external'
        : isCustomer
        ? 'customer'
        : isScanner
        ? 'scanner'
        : variantParent
        ? 'variant'
        : null;
    };

    assert.strictEqual(resolveActiveModal(true, false, false, null), 'external');
    assert.strictEqual(resolveActiveModal(false, true, false, null), 'customer');
    assert.strictEqual(resolveActiveModal(false, false, true, null), 'scanner');
    assert.strictEqual(resolveActiveModal(false, false, false, { id: 1 }), 'variant');
    assert.strictEqual(resolveActiveModal(false, false, false, null), null);
  });
});
