import { describe, it } from 'node:test';
import assert from 'node:assert';
import {
  STOCK_COUNT_SCOPE,
  buildStockCountCreatePayload,
  isOutOfScopeScanError,
  resolveStockCountScopeDisplay,
} from './StockCountPage.jsx';

describe('StockCountPage — product scope helpers (WU3)', () => {
  it('1. builds the All payload without category or product ids', () => {
    const payload = buildStockCountCreatePayload({
      name: '  Conteo  ',
      scopeType: STOCK_COUNT_SCOPE.All,
      scopeCategoryId: '5',
      scopeProducts: [{ id: 10 }],
      notes: '  notas  ',
    });

    assert.deepStrictEqual(payload, {
      name: 'Conteo',
      scopeType: 0,
      scopeCategoryId: null,
      scopeProductIds: null,
      notes: 'notas',
    });
  });

  it('2. builds the Category payload with the numeric category and no products', () => {
    const payload = buildStockCountCreatePayload({
      name: 'Categoría',
      scopeType: STOCK_COUNT_SCOPE.Category,
      scopeCategoryId: '5',
      scopeProducts: [{ id: 10 }],
      notes: '',
    });

    assert.deepStrictEqual(payload, {
      name: 'Categoría',
      scopeType: 1,
      scopeCategoryId: 5,
      scopeProductIds: null,
      notes: null,
    });
  });

  it('3. builds the Products payload with unique positive ids only', () => {
    const payload = buildStockCountCreatePayload({
      name: 'Focal',
      scopeType: STOCK_COUNT_SCOPE.Products,
      scopeCategoryId: '5',
      scopeProducts: [{ id: 10 }, { id: '20' }, { id: -1 }, { id: 'x' }],
      notes: 'notas',
    });

    assert.deepStrictEqual(payload, {
      name: 'Focal',
      scopeType: 2,
      scopeCategoryId: null,
      scopeProductIds: [10, 20],
      notes: 'notas',
    });
  });

  it('4. resolves the shared scope label with fallbacks', () => {
    assert.strictEqual(
      resolveStockCountScopeDisplay({ scopeDisplay: 'Productos seleccionados (2)' }),
      'Productos seleccionados (2)'
    );
    assert.strictEqual(resolveStockCountScopeDisplay({ scopeCategoryPath: 'A > B' }), 'A > B');
    assert.strictEqual(resolveStockCountScopeDisplay({}), 'Todo el inventario');
  });

  it('5. recognizes only 409 out-of-scope scan errors as a scope warning', () => {
    assert.strictEqual(
      isOutOfScopeScanError({
        status: 409,
        message: "El producto 'X' no pertenece a los productos seleccionados de la sesión de conteo #1.",
      }),
      true
    );
    assert.strictEqual(
      isOutOfScopeScanError({ status: 409, message: 'La sesión no está abierta.' }),
      false
    );
    assert.strictEqual(
      isOutOfScopeScanError({ status: 400, message: 'productos seleccionados' }),
      false
    );
    assert.strictEqual(isOutOfScopeScanError(null), false);
  });
});
