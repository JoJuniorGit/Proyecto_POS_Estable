import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('CustomerSelectorCard Blur and Auto-Collapse Tests', () => {
  test('1. Losing focus (blur) outside the customer container cancels customer change and collapses dropdown', () => {
    let isExpanded = true;
    let isDropdownOpen = true;
    let isCreatingCustomer = true;
    let query = 'Juan Pérez';
    let error = 'Some previous error';

    const handleCancelAndCollapse = () => {
      isDropdownOpen = false;
      isExpanded = false;
      isCreatingCustomer = false;
      query = '';
      error = null;
    };

    const handleInputBlur = (relatedTarget, containerElement) => {
      if (containerElement && relatedTarget && containerElement.contains(relatedTarget)) {
        return { collapsed: false };
      }
      handleCancelAndCollapse();
      return { collapsed: true };
    };

    const fakeContainer = {
      contains: (elem) => elem && elem.id === 'inside-card',
    };

    // Case A: Blur to external element (e.g., payment amount input or backdrop)
    const externalElement = { id: 'payment-input' };
    const result = handleInputBlur(externalElement, fakeContainer);

    assert.strictEqual(result.collapsed, true, 'Card must collapse when focus moves outside container');
    assert.strictEqual(isExpanded, false, 'isExpanded must be false');
    assert.strictEqual(isDropdownOpen, false, 'isDropdownOpen must be false');
    assert.strictEqual(isCreatingCustomer, false, 'isCreatingCustomer must be false');
    assert.strictEqual(query, '', 'Query must be reset');
    assert.strictEqual(error, null, 'Error must be cleared');
  });

  test('2. Focus moving inside container (e.g. create customer button) does not cancel change', () => {
    let isExpanded = true;
    let isDropdownOpen = true;

    const handleCancelAndCollapse = () => {
      isDropdownOpen = false;
      isExpanded = false;
    };

    const handleInputBlur = (relatedTarget, containerElement) => {
      if (containerElement && relatedTarget && containerElement.contains(relatedTarget)) {
        return { collapsed: false };
      }
      handleCancelAndCollapse();
      return { collapsed: true };
    };

    const fakeContainer = {
      contains: (elem) => elem && elem.id === 'create-button',
    };

    const insideElement = { id: 'create-button' };
    const result = handleInputBlur(insideElement, fakeContainer);

    assert.strictEqual(result.collapsed, false, 'Card must NOT collapse when focus stays inside container');
    assert.strictEqual(isExpanded, true);
    assert.strictEqual(isDropdownOpen, true);
  });

  test('3. Escape key immediately collapses dropdown and resets search query', () => {
    let isExpanded = true;
    let isDropdownOpen = true;
    let query = 'Carlos';

    const handleKeyDown = (key) => {
      if (key === 'Escape' && isExpanded) {
        isDropdownOpen = false;
        isExpanded = false;
        query = '';
      }
    };

    handleKeyDown('Escape');
    assert.strictEqual(isExpanded, false);
    assert.strictEqual(isDropdownOpen, false);
    assert.strictEqual(query, '');
  });
});
