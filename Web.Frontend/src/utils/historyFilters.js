export const TEST_CASHIER_NAME = 'BOT_STRESS_TEST';

export function filterHistorySales(sales, { cashierFilter = '', hideTestSales = false } = {}) {
  const term = (cashierFilter || '').trim().toLowerCase();
  const visibleSales = sales.filter((sale) => {
    const cashierName = (sale.cashierName || '').trim();
    if (term && !cashierName.toLowerCase().includes(term)) return false;
    if (hideTestSales && cashierName.toUpperCase() === TEST_CASHIER_NAME) return false;
    return true;
  });
  return { visibleSales, hiddenCount: sales.length - visibleSales.length };
}

export function accumulateCashierNames(cashiers, sales) {
  const map = new Map(cashiers.map((c) => [c.toLowerCase(), c]));
  for (const sale of sales) {
    const name = (sale.cashierName || '').trim();
    if (name && !map.has(name.toLowerCase())) map.set(name.toLowerCase(), name);
  }
  return [...map.values()].sort((a, b) => a.localeCompare(b));
}

export function areSecondaryFiltersActive({ cashierFilter = '', hideTestSales = false } = {}) {
  return hideTestSales || Boolean((cashierFilter || '').trim());
}

export function applySecondaryFilterDrafts(active, draft) {
  return {
    cashierFilter: typeof draft.cashierFilter === 'string' ? draft.cashierFilter : active.cashierFilter,
    hideTestSales: typeof draft.hideTestSales === 'boolean' ? draft.hideTestSales : active.hideTestSales,
  };
}