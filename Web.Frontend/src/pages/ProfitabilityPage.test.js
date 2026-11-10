// F4-WU2 (Paso 14): dashboard web de rentabilidad. El runner no tiene DOM, por lo que se usa
// montaje SSR (renderToString) sobre el componente presentacional y sobre la página con RoleGuard.
import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import ProfitabilityPage, { ProfitabilityDashboard } from './ProfitabilityPage.jsx';
import { AuthProvider } from '../context/AuthContext';

let profile = null;

globalThis.sessionStorage = {
  getItem: () => (profile ? JSON.stringify(profile) : null),
  setItem: () => {},
  removeItem: () => {},
  clear: () => {},
};

const SUMMARY = {
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

const EMPTY_SUMMARY = {
  ...SUMMARY,
  breakdown: {
    series: [],
    paymentMethods: [],
    categories: [],
    products: [],
    sellers: [],
    alerts: [],
  },
  alerts: [],
};

function renderPage(role) {
  profile = role === undefined ? null : { id: 1, name: 'Test User', cedula: 'V-1', role };
  return renderToString(
    <AuthProvider>
      <ProfitabilityPage />
    </AuthProvider>
  );
}

function renderDashboard(props = {}) {
  return renderToString(
    <ProfitabilityDashboard
      summary={SUMMARY}
      viewMode="both"
      onViewModeChange={() => {}}
      {...props}
    />
  );
}

describe('ProfitabilityPage — role gating', () => {
  it('1. Cashier is denied and never sees the dashboard', () => {
    const html = renderPage('Cashier');
    assert.match(html, /Acceso Denegado/);
    assert.doesNotMatch(html, /UTILIDAD DEVENGADA/);
    assert.doesNotMatch(html, /Exportar CSV/);
  });

  it('2. Manager and Admin can open the dashboard', () => {
    for (const role of ['Manager', 'Admin', 2, 3]) {
      const html = renderPage(role);
      assert.doesNotMatch(html, /Acceso Denegado/, `${role} must be allowed`);
      assert.match(html, /UTILIDAD DEVENGADA/, `${role} must see the headline KPI`);
    }
  });

  it('3. exposes the period, filter and export controls', () => {
    const html = renderPage('Admin');
    assert.match(html, /Rentabilidad/);
    assert.match(html, /Hoy/);
    assert.match(html, /Ayer/);
    assert.match(html, /Semana/);
    assert.match(html, /Mes/);
    assert.match(html, /Personalizado/);
    assert.match(html, /Categoría/);
    assert.match(html, /Producto/);
    assert.match(html, /Cajero/);
    assert.match(html, /Tipo de venta/);
    assert.match(html, /Exportar CSV/);
    assert.match(html, /Exportar XLSX/);
    assert.match(html, /Exportar PDF/);
  });
});

describe('ProfitabilityDashboard — KPIs, toggle and coverage', () => {
  it('4. renders the headline, secondary and detail KPIs', () => {
    const html = renderDashboard();

    assert.match(html, /UTILIDAD DEVENGADA/);
    assert.match(html, /\$ 650,00/);
    assert.match(html, /UTILIDAD PERCIBIDA EN CAJA/);
    assert.match(html, /\$ 565,00/);
    assert.match(html, /VENTAS NETAS/);
    assert.match(html, /\$ 1\.850,00/);
    assert.match(html, /CMV/);
    assert.match(html, /\$ 900,00/);
    assert.match(html, /UTILIDAD BRUTA/);
    assert.match(html, /MARGEN BRUTO/);
    assert.match(html, /40,00 %/);
    assert.match(html, /COMISIONES DE SERVICIO/);
    assert.match(html, /DIFERENCIA FX \(INFORMATIVA\)/);
    assert.match(html, /CAJA CHICA/);
    assert.match(html, /COMISIONES DE MEDIOS DE PAGO/);
    assert.match(html, /UTILIDAD NETA/);
  });

  it('5. renders the coverage banner with cost, fee and unknown-data counts', () => {
    const html = renderDashboard();

    assert.match(html, /Cobertura de datos históricos/);
    assert.match(html, /Costo:/);
    assert.match(html, /81,08 %/);
    assert.match(html, /Comisiones:/);
    assert.match(html, /92,50 %/);
    assert.match(html, /Ingreso sin costo:/);
    assert.match(html, /4 líneas/);
    assert.match(html, /Cobros sin fee:/);
    assert.match(html, /2 pagos/);
    assert.match(html, /no se prorratean artificialmente por artículo/);
  });

  it('6. renders the Devengado/Percibido/Ambos toggle with a single active mode', () => {
    const html = renderDashboard({ viewMode: 'both' });

    assert.match(html, /Devengado/);
    assert.match(html, /Percibido/);
    assert.match(html, /Ambos/);
    assert.strictEqual((html.match(/aria-pressed="true"/g) || []).length, 1);
    assert.match(html, /aria-pressed="true"[^>]*>Ambos/);
  });

  it('7. filters the series by the selected view mode', () => {
    const accrued = renderDashboard({ viewMode: 'accrued' });
    assert.match(accrued, /data-series="accrued"/);
    assert.doesNotMatch(accrued, /data-series="realized"/);

    const realized = renderDashboard({ viewMode: 'realized' });
    assert.match(realized, /data-series="realized"/);
    assert.doesNotMatch(realized, /data-series="accrued"/);

    const both = renderDashboard({ viewMode: 'both' });
    assert.match(both, /data-series="accrued"/);
    assert.match(both, /data-series="realized"/);
  });
});

describe('ProfitabilityDashboard — native charts and tables', () => {
  it('8. renders a native SVG series chart without chart libraries', () => {
    const html = renderDashboard();

    assert.match(html, /data-testid="profitability-series-chart"/);
    assert.match(html, /<svg/);
    assert.match(html, /<rect[^>]*data-series="accrued"/);
    assert.match(html, /<rect[^>]*data-series="realized"/);
    assert.match(html, /Serie temporal: utilidad neta/);
  });

  it('9. renders payment method, product, category, seller and series tables', () => {
    const html = renderDashboard();

    assert.match(html, /Métodos de pago/);
    assert.match(html, /Punto de Venta/);
    assert.match(html, /Pago Móvil/);
    assert.match(html, /data-testid="payment-method-bar"/);

    assert.match(html, /Productos/);
    assert.match(html, /Café 500g/);
    assert.match(html, /Galletas/);
    assert.match(html, /data-testid="product-row"/);

    assert.match(html, /Categorías/);
    assert.match(html, /Bebidas/);
    assert.match(html, /Snacks/);

    assert.match(html, /Vendedores \/ cajeros/);
    assert.match(html, /Ana Pérez/);
    assert.match(html, /Luis Gómez/);

    assert.match(html, /data-testid="series-table"/);
    assert.match(html, /01\/11/);
    assert.match(html, /02\/11/);
  });

  it('10. renders margin erosion alerts', () => {
    const html = renderDashboard();

    assert.match(html, /Alertas de erosión de margen/);
    assert.match(html, /data-testid="alerts-table"/);
    assert.match(html, /El margen bruto cayó 6,00 puntos/);
    assert.match(html, /Advertencia/);
  });
});

describe('ProfitabilityDashboard — empty, error and loading states', () => {
  it('11. renders the empty state when the period has no data', () => {
    const html = renderDashboard({ summary: EMPTY_SUMMARY });

    assert.match(html, /Sin movimientos para el período seleccionado/);
    assert.match(html, /Ajuste el rango de fechas o los filtros/);
  });

  it('12. renders the error message from the API', () => {
    const html = renderDashboard({
      summary: null,
      error: 'No se pudo cargar la rentabilidad: Error 403.',
    });

    assert.match(html, /No se pudo cargar la rentabilidad: Error 403\./);
    assert.match(html, /alert-danger/);
  });

  it('13. renders a loading state while the request is in flight', () => {
    const html = renderDashboard({ summary: null, isLoading: true });

    assert.match(html, /Cargando rentabilidad/);
    assert.doesNotMatch(html, /Sin movimientos para el período seleccionado/);
  });
});
