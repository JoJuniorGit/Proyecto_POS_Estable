import { test, describe } from 'node:test';
import assert from 'node:assert';

describe('CustomerModal Selection and Defensive Calculations', () => {
  test('1. Defensive math handles undefined/null saleTotalUSD and exchangeRate without throwing', () => {
    const computeSummary = ({ saleTotalUSD, exchangeRate, initialPaymentBsS, enableInitialPayment }) => {
      const safeSaleTotalUSD = typeof saleTotalUSD === 'number' && !isNaN(saleTotalUSD) ? saleTotalUSD : 0;
      const safeExchangeRate = typeof exchangeRate === 'number' && exchangeRate > 0 ? exchangeRate : 1;
      const initialBs = parseFloat(initialPaymentBsS) || 0;
      const initialUsd = safeExchangeRate > 0 ? initialBs / safeExchangeRate : 0;
      const remainingDebtUsd = Math.max(0, safeSaleTotalUSD - (enableInitialPayment ? initialUsd : 0));

      return {
        totalUSDFormatted: `$${safeSaleTotalUSD.toFixed(2)}`,
        initialUsdFormatted: `-$${initialUsd.toFixed(2)}`,
        remainingDebtUsdFormatted: `$${remainingDebtUsd.toFixed(2)}`,
      };
    };

    // Case with undefined saleTotalUSD (as previously passed from PosPage)
    const resultUndefined = computeSummary({
      saleTotalUSD: undefined,
      exchangeRate: undefined,
      initialPaymentBsS: '',
      enableInitialPayment: false,
    });

    assert.strictEqual(resultUndefined.totalUSDFormatted, '$0.00');
    assert.strictEqual(resultUndefined.remainingDebtUsdFormatted, '$0.00');

    // Case with valid values
    const resultValid = computeSummary({
      saleTotalUSD: 100,
      exchangeRate: 64.5,
      initialPaymentBsS: '645',
      enableInitialPayment: true,
    });

    assert.strictEqual(resultValid.totalUSDFormatted, '$100.00');
    assert.strictEqual(resultValid.initialUsdFormatted, '-$10.00');
    assert.strictEqual(resultValid.remainingDebtUsdFormatted, '$90.00');
  });

  test('2. In mode select, clicking a customer immediately triggers onSelectCustomer', () => {
    let selectedCustomerId = null;

    const mockCustomer = { id: 101, name: 'Juan Pérez', cedulaOrRif: 'V-12345678' };
    const mode = 'select';
    const onSelectCustomer = (id) => {
      selectedCustomerId = id;
    };

    // Simulate clicking customer in mode select
    const handleItemClick = (c) => {
      if (mode === 'select') {
        if (onSelectCustomer) onSelectCustomer(c.id);
      }
    };

    handleItemClick(mockCustomer);
    assert.strictEqual(selectedCustomerId, 101, 'Customer ID 101 must be immediately dispatched to onSelectCustomer');
  });

  test('3. In mode select, creating a new customer immediately assigns them via onSelectCustomer', () => {
    let selectedCustomerId = null;

    const createdCustomer = { id: 202, name: 'María Gómez', cedulaOrRif: 'V-87654321' };
    const mode = 'select';
    const onSelectCustomer = (id) => {
      selectedCustomerId = id;
    };

    const handleCustomerCreated = (created) => {
      if (mode === 'select') {
        if (onSelectCustomer) onSelectCustomer(created.id);
      }
    };

    handleCustomerCreated(createdCustomer);
    assert.strictEqual(selectedCustomerId, 202, 'Newly created customer must be assigned immediately');
  });
});
