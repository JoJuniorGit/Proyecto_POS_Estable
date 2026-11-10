import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

/**
 * F5-WU1 (Paso 14): journey real de Admin sobre el dashboard de rentabilidad.
 * El backend se simula de forma determinista para que el E2E valide la página, el contrato
 * summary/export y el toggle sin depender de PostgreSQL ni de ventas del día.
 */
const SUMMARY_FIXTURE = {
  fromUtc: '2026-11-01T04:00:00Z',
  toUtc: '2026-11-11T04:00:00Z',
  timeZoneId: 'America/Caracas',
  reportCurrencyCode: 'USD',
  reportCurrencySymbol: '$',
  isSingleCurrency: false,
  generatedAtUtc: '2026-11-10T15:00:00Z',
  globalCostsNote:
    'PettyCashExpenses, ServiceCommissions y PaymentMethodFees son gastos/ingresos globales de operación y no se prorratean artificialmente por artículo.',
  accrued: {
    grossSales: 2000,
    cancellations: 50,
    returns: 100,
    netSales: 1850,
    knownCostRevenue: 1500,
    unknownCostRevenue: 350,
    cogs: 900,
    grossProfit: 600,
    grossMarginPercent: 40,
    serviceCommissions: 120,
    fxDifferenceInformational: 30,
    pettyCashExpenses: 45,
    paymentMethodFees: 25,
    netProfit: 650,
    netMarginPercent: 35.14,
  },
  realized: {
    collectedRevenue: 1600,
    knownCostCollectedRevenue: 1300,
    unknownCostCollectedRevenue: 300,
    realizedCogs: 780,
    realizedGrossProfit: 520,
    realizedGrossMarginPercent: 40,
    serviceCommissions: 90,
    fxDifference: 20,
    pettyCashExpenses: 45,
    paymentMethodFees: 20,
    netProfit: 565,
    netMarginPercent: 35.31,
  },
  coverage: {
    costCoveragePercent: 81.08,
    knownCostRevenue: 1500,
    knownCogs: 900,
    unknownCostRevenue: 350,
    unknownCostLinesCount: 4,
    feeCoveragePercent: 92.5,
    knownFeePaymentAmount: 1480,
    unknownFeePaymentAmount: 120,
    unknownFeePaymentsCount: 2,
  },
  breakdown: {
    series: [
      {
        periodStartUtc: '2026-11-01T04:00:00Z',
        label: '01/11',
        accruedNetProfit: 300,
        realizedNetProfit: 250,
        accruedNetSales: 900,
        realizedCollectedRevenue: 800,
      },
      {
        periodStartUtc: '2026-11-02T04:00:00Z',
        label: '02/11',
        accruedNetProfit: 350,
        realizedNetProfit: 315,
        accruedNetSales: 950,
        realizedCollectedRevenue: 800,
      },
    ],
    paymentMethods: [
      {
        paymentMethodId: 1,
        paymentMethodName: 'Punto de Venta',
        collectedAmount: 1200,
        feeAmount: 18,
        knownFeeAmount: 1200,
        unknownFeeAmount: 0,
        feeCoveragePercent: 100,
      },
      {
        paymentMethodId: 2,
        paymentMethodName: 'Pago Móvil',
        collectedAmount: 400,
        feeAmount: 7,
        knownFeeAmount: 280,
        unknownFeeAmount: 120,
        feeCoveragePercent: 70,
      },
    ],
    categories: [
      {
        categoryId: 3,
        categoryName: 'Bebidas',
        revenue: 1000,
        cogs: 600,
        grossProfit: 400,
        grossMarginPercent: 40,
        contributionPercent: 54.05,
      },
      {
        categoryId: 4,
        categoryName: 'Snacks',
        revenue: 850,
        cogs: 300,
        grossProfit: 550,
        grossMarginPercent: 64.71,
        contributionPercent: 45.95,
      },
    ],
    products: [
      {
        productId: 11,
        productName: 'Café 500g',
        categoryId: 3,
        categoryName: 'Bebidas',
        quantitySold: 20,
        revenue: 600,
        cogs: 360,
        grossProfit: 240,
        grossMarginPercent: 40,
      },
      {
        productId: 12,
        productName: 'Galletas',
        categoryId: 4,
        categoryName: 'Snacks',
        quantitySold: 35,
        revenue: 400,
        cogs: 120,
        grossProfit: 280,
        grossMarginPercent: 70,
      },
    ],
    sellers: [
      { cashierId: 9, cashierName: 'Ana Pérez', netSales: 1200, grossProfit: 420 },
      { cashierId: 10, cashierName: 'Luis Gómez', netSales: 650, grossProfit: 180 },
    ],
    alerts: [
      {
        code: 'MARGIN_EROSION',
        severity: 1,
        message: 'El margen bruto cayó 6,00 puntos frente al período anterior.',
        currentValue: 40,
        previousValue: 46,
        delta: -6,
      },
    ],
  },
  alerts: [
    {
      code: 'MARGIN_EROSION',
      severity: 1,
      message: 'El margen bruto cayó 6,00 puntos frente al período anterior.',
      currentValue: 40,
      previousValue: 46,
      delta: -6,
    },
  ],
};

/**
 * El mock se registra DESPUÉS del login para no interferir con el bootstrap de auth del helper.
 * Una sola ruta cubre summary, breakdown y el binario de exportación.
 */
async function mockProfitabilityApi(page) {
  await page.route('**/api/profitability/**', async (route) => {
    const url = new URL(route.request().url());

    if (url.pathname.endsWith('/export')) {
      return route.fulfill({
        status: 200,
        headers: {
          'content-type': 'text/csv; charset=utf-8',
          'content-disposition':
            'attachment; filename="Estado_Resultados_20261101_20261110.csv"',
        },
        body: [
          'RESUMEN DEVENGADO',
          'Utilidad neta devengada;650.00',
          'RESUMEN PERCIBIDO',
          'Utilidad neta percibida;565.00',
        ].join('\r\n'),
      });
    }

    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(SUMMARY_FIXTURE),
    });
  });
}

test.describe('Profitability dashboard (Admin journey)', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
    await mockProfitabilityApi(page);
  });

  test('renders KPIs, coverage, native chart, tables, export and the view-mode toggle', async ({ page }) => {
    await page.goto('/#profitability');
    await expect(page).toHaveURL(/#profitability$/);

    await expect(page.getByRole('heading', { name: 'Rentabilidad' })).toBeVisible();
    await expect(page.getByTestId('profitability-dashboard')).toBeVisible();

    // KPIs headline/secundario y detalle.
    const accruedKpi = page.getByTestId('kpi-utilidad-devengada');
    const realizedKpi = page.getByTestId('kpi-utilidad-percibida');
    await expect(accruedKpi).toContainText('UTILIDAD DEVENGADA');
    await expect(accruedKpi).toContainText('$ 650,00');
    await expect(realizedKpi).toContainText('UTILIDAD PERCIBIDA EN CAJA');
    await expect(realizedKpi).toContainText('$ 565,00');
    await expect(page.getByText('VENTAS NETAS', { exact: true })).toBeVisible();
    await expect(page.getByText('CMV', { exact: true })).toBeVisible();
    await expect(page.getByText('MARGEN BRUTO', { exact: true })).toBeVisible();
    await expect(page.getByText('COMISIONES DE MEDIOS DE PAGO', { exact: true })).toBeVisible();
    await expect(page.getByText('MARGEN NETO', { exact: true })).toBeVisible();

    // Banner de cobertura con costos, fees y datos desconocidos.
    const coverage = page.getByTestId('coverage-banner');
    await expect(coverage).toContainText('Cobertura de datos históricos');
    await expect(coverage).toContainText('Costo:');
    await expect(coverage).toContainText('81,08 %');
    await expect(coverage).toContainText('Comisiones:');
    await expect(coverage).toContainText('92,50 %');
    await expect(coverage).toContainText('Ingreso sin costo:');
    await expect(coverage).toContainText('4 líneas');
    await expect(coverage).toContainText('Cobros sin fee:');
    await expect(coverage).toContainText('2 pagos');

    // Gráfico SVG nativo con ambas series en el modo por defecto (Ambos).
    const chart = page.getByTestId('profitability-series-chart');
    await expect(chart).toBeVisible();
    await expect(page.getByText('Serie temporal: utilidad neta')).toBeVisible();
    await expect(chart.locator('rect[data-series="accrued"]')).toHaveCount(2);
    await expect(chart.locator('rect[data-series="realized"]')).toHaveCount(2);

    // Tablas de series, métodos, productos, categorías, vendedores y alertas.
    const cardByTitle = (title) =>
      page
        .locator('.profitability-table-card')
        .filter({ has: page.getByRole('heading', { name: title, exact: true }) });

    const methodsCard = cardByTitle('Métodos de pago');
    const productsCard = cardByTitle('Productos');
    const categoriesCard = cardByTitle('Categorías');
    const sellersCard = cardByTitle('Vendedores / cajeros');

    await expect(page.getByTestId('series-table')).toBeVisible();
    await expect(page.getByText('Métodos de pago', { exact: true })).toBeVisible();
    await expect(methodsCard.getByRole('cell', { name: 'Punto de Venta' })).toBeVisible();
    await expect(methodsCard.getByRole('cell', { name: 'Pago Móvil' })).toBeVisible();
    await expect(page.getByText('Productos', { exact: true })).toBeVisible();
    await expect(page.getByTestId('product-row')).toHaveCount(2);
    await expect(productsCard.getByRole('cell', { name: 'Café 500g' })).toBeVisible();
    await expect(productsCard.getByRole('cell', { name: 'Galletas' })).toBeVisible();
    await expect(page.getByText('Categorías', { exact: true })).toBeVisible();
    await expect(categoriesCard.getByRole('cell', { name: 'Bebidas' })).toBeVisible();
    await expect(categoriesCard.getByRole('cell', { name: 'Snacks' })).toBeVisible();
    await expect(page.getByText('Vendedores / cajeros', { exact: true })).toBeVisible();
    await expect(sellersCard.getByRole('cell', { name: 'Ana Pérez' })).toBeVisible();
    await expect(sellersCard.getByRole('cell', { name: 'Luis Gómez' })).toBeVisible();
    await expect(page.getByTestId('alerts-table')).toBeVisible();
    await expect(page.getByText('El margen bruto cayó 6,00 puntos frente al período anterior.')).toBeVisible();

    // Controles de exportación.
    await expect(page.getByRole('button', { name: 'Exportar CSV' })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Exportar XLSX' })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Exportar PDF' })).toBeEnabled();

    // Toggle: Ambos (default) -> Percibido -> Devengado -> Ambos.
    const viewToggle = page.getByRole('group', { name: 'Vista de rentabilidad' });
    const accruedButton = viewToggle.getByRole('button', { name: 'Devengado' });
    const realizedButton = viewToggle.getByRole('button', { name: 'Percibido' });
    const bothButton = viewToggle.getByRole('button', { name: 'Ambos' });

    await expect(bothButton).toHaveAttribute('aria-pressed', 'true');
    await expect(accruedButton).toHaveAttribute('aria-pressed', 'false');
    await expect(realizedButton).toHaveAttribute('aria-pressed', 'false');

    await realizedButton.click();
    await expect(realizedButton).toHaveAttribute('aria-pressed', 'true');
    await expect(accruedButton).toHaveAttribute('aria-pressed', 'false');
    await expect(chart.locator('rect[data-series="realized"]')).toHaveCount(2);
    await expect(chart.locator('rect[data-series="accrued"]')).toHaveCount(0);
    await expect(page.getByTestId('series-table')).toContainText('Utilidad percibida');
    await expect(page.getByTestId('series-table')).not.toContainText('Utilidad devengada');

    await accruedButton.click();
    await expect(accruedButton).toHaveAttribute('aria-pressed', 'true');
    await expect(realizedButton).toHaveAttribute('aria-pressed', 'false');
    await expect(chart.locator('rect[data-series="accrued"]')).toHaveCount(2);
    await expect(chart.locator('rect[data-series="realized"]')).toHaveCount(0);
    await expect(page.getByTestId('series-table')).toContainText('Utilidad devengada');
    await expect(page.getByTestId('series-table')).not.toContainText('Utilidad percibida');

    await bothButton.click();
    await expect(bothButton).toHaveAttribute('aria-pressed', 'true');
    await expect(chart.locator('rect[data-series="accrued"]')).toHaveCount(2);
    await expect(chart.locator('rect[data-series="realized"]')).toHaveCount(2);

    // Export CSV: el endpoint simulado responde el binario y la página confirma la exportación.
    await page.getByRole('button', { name: 'Exportar CSV' }).click();
    await expect(page.locator('.profitability-export-status')).toContainText(
      'Reporte CSV exportado correctamente.'
    );
  });
});
