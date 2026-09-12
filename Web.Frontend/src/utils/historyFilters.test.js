import { test, describe } from 'node:test';
import assert from 'node:assert';
import { TEST_CASHIER_NAME, filterHistorySales, accumulateCashierNames, areSecondaryFiltersActive, applySecondaryFilterDrafts } from './historyFilters.js';

const sale = (id, cashierName) => ({ id, cashierName, totalBsS: 1 });

describe('historyFilters [8.108]', () => {
  test('filterHistorySales_ConFiltroVacioYToggleOff_NoOcultaVentas', () => {
    const sales = [sale(1, 'Juan'), sale(2, TEST_CASHIER_NAME)];
    const { visibleSales, hiddenCount } = filterHistorySales(sales, { cashierFilter: '', hideTestSales: false });
    assert.strictEqual(visibleSales.length, 2);
    assert.strictEqual(hiddenCount, 0);
  });

  test('filterHistorySales_ConToggleOn_OcultaSoloCajeroDePrueba', () => {
    const sales = [sale(1, 'Juan'), sale(2, TEST_CASHIER_NAME), sale(3, TEST_CASHIER_NAME.toLowerCase())];
    const { visibleSales, hiddenCount } = filterHistorySales(sales, { cashierFilter: '', hideTestSales: true });
    assert.deepStrictEqual(visibleSales.map((s) => s.id), [1]);
    assert.strictEqual(hiddenCount, 2);
  });

  test('filterHistorySales_ConSubstring_CoincideIgnorandoMayusculas', () => {
    const sales = [sale(1, 'María Pérez'), sale(2, 'maría'), sale(3, 'Pedro')];
    const { visibleSales } = filterHistorySales(sales, { cashierFilter: 'MARÍA', hideTestSales: false });
    assert.deepStrictEqual(visibleSales.map((s) => s.id), [1, 2]);
  });

  test('accumulateCashierNames_AcumulaYSinDuplicados_SinPerderCasingOriginal', () => {
    const first = accumulateCashierNames([], [sale(1, 'BOT_STRESS_TEST'), sale(2, 'Ana')]);
    assert.deepStrictEqual(first, ['Ana', 'BOT_STRESS_TEST']);
    const merged = accumulateCashierNames(first, [sale(3, 'bot_stress_test'), sale(4, 'Carlos')]);
    assert.deepStrictEqual(merged, ['Ana', 'BOT_STRESS_TEST', 'Carlos']);
  });
});

describe('historyFilters flyout [8.111]', () => {
  test('areSecondaryFiltersActive_ConFiltrosVacios_EsFalso', () => {
    assert.strictEqual(areSecondaryFiltersActive({ cashierFilter: '', hideTestSales: false }), false);
    assert.strictEqual(areSecondaryFiltersActive({ cashierFilter: '   ', hideTestSales: false }), false);
    assert.strictEqual(areSecondaryFiltersActive({}), false);
  });

  test('areSecondaryFiltersActive_ConCajeroOToggleActivo_EsVerdadero', () => {
    assert.strictEqual(areSecondaryFiltersActive({ cashierFilter: 'ana', hideTestSales: false }), true);
    assert.strictEqual(areSecondaryFiltersActive({ cashierFilter: '', hideTestSales: true }), true);
  });

  test('applySecondaryFilterDrafts_ConBorradoresValidos_FusionaActivos', () => {
    const next = applySecondaryFilterDrafts(
      { cashierFilter: 'ana', hideTestSales: false },
      { cashierFilter: 'carlos', hideTestSales: true }
    );
    assert.deepStrictEqual(next, { cashierFilter: 'carlos', hideTestSales: true });
  });

  test('applySecondaryFilterDrafts_ConBorradorVacio_LimpiaFiltroActivo', () => {
    const next = applySecondaryFilterDrafts(
      { cashierFilter: 'ana', hideTestSales: true },
      { cashierFilter: '', hideTestSales: false }
    );
    assert.deepStrictEqual(next, { cashierFilter: '', hideTestSales: false });
  });

  test('applySecondaryFilterDrafts_ConTiposNoEsperados_ConservaActivos', () => {
    const next = applySecondaryFilterDrafts(
      { cashierFilter: 'ana', hideTestSales: true },
      { cashierFilter: undefined, hideTestSales: undefined }
    );
    assert.deepStrictEqual(next, { cashierFilter: 'ana', hideTestSales: true });
  });
});