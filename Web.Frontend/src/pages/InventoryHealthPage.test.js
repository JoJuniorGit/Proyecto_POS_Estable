import { describe, it } from 'node:test';
import assert from 'node:assert';
import { renderToString } from 'react-dom/server';
import React from 'react';
import {
  InventoryHealthDashboard,
  clampHealthPercent,
  formatHealthMoney,
  formatHealthPercent,
  inventoryHealthEmpty,
} from './InventoryHealthPage.jsx';
import {
  buildDeadStockPromotionRequest,
  buildInventoryHealthQuery,
} from '../services/inventoryHealthApi.js';

const REPORT = {
  summary: {
    totalProducts: 4,
    highRotationCount: 1,
    mediumRotationCount: 1,
    lowRotationCount: 1,
    deadStockCount: 1,
    highRotationPercent: 25,
    mediumRotationPercent: 25,
    lowRotationPercent: 25,
    deadStockPercent: 25,
    totalInventoryValueUsd: 400,
    immobilizedCapitalUsd: 100,
    immobilizedCapitalPercent: 25,
  },
  products: [
    {
      productId: 7,
      name: 'Queso añejo',
      sku: 'QUESO-7',
      categoryName: 'Lacteos',
      stockQuantity: 10,
      inventoryValueUsd: 100,
      unitsSold: 0,
      salesVelocityPerDay: 0,
      averageInventoryDays: null,
      lastSaleDateUtc: null,
      daysSinceLastSale: 90,
      rotationClass: 'DeadStock',
      rotationClassLabel: 'Muerto',
    },
  ],
  categories: [
    {
      categoryId: 3,
      categoryName: 'Lacteos',
      productCount: 1,
      stockQuantity: 10,
      inventoryValueUsd: 100,
      unitsSold: 0,
      averageInventoryDays: null,
      deadStockCount: 1,
      immobilizedCapitalUsd: 100,
      rotationClass: 'Low',
      rotationClassLabel: 'Baja',
    },
  ],
  page: 1,
  totalPages: 1,
  totalCount: 1,
};

describe('InventoryHealthPage (Paso 15)', () => {
  it('builds a server-side query with the selected window and dead-stock threshold', () => {
    const query = buildInventoryHealthQuery({
      salesWindowDays: 60,
      deadStockDays: 45,
      categoryId: 9,
      searchText: 'queso añejo',
      rotationClass: 'DeadStock',
      page: 2,
      pageSize: 20,
    });

    assert.match(query, /salesWindowDays=60/);
    assert.match(query, /deadStockDays=45/);
    assert.match(query, /categoryId=9/);
    assert.match(query, /searchText=queso\+a%C3%B1ejo/);
    assert.match(query, /rotationClass=DeadStock/);
    assert.match(query, /page=2/);
  });

  it('maps a dead-stock selection to the Paso 9 bulk price contract', () => {
    const request = buildDeadStockPromotionRequest([3, 8], 15);

    assert.deepEqual(request.productIds, [3, 8]);
    assert.equal(request.selectAllFiltered, false);
    assert.deepEqual(request.operations, [
      { field: 0, mode: 2, value: 15 },
    ]);
    assert.throws(() => buildDeadStockPromotionRequest([], 10), /No hay productos/);
    assert.throws(() => buildDeadStockPromotionRequest([3], 0), /porcentaje/);
  });

  it('formats executive amounts and clamps chart bars', () => {
    assert.equal(formatHealthMoney(1234.5), '$ 1.234,50');
    assert.equal(formatHealthPercent(25), '25,0 %');
    assert.equal(clampHealthPercent(-5), 0);
    assert.equal(clampHealthPercent(140), 100);
    assert.equal(inventoryHealthEmpty({ products: [] }), true);
  });

  it('renders KPIs, dead-stock badge and the category table', () => {
    const html = renderToString(
      React.createElement(InventoryHealthDashboard, {
        report: REPORT,
        loading: false,
        error: '',
        filters: {
          salesWindowDays: 90,
          deadStockDays: 60,
          categoryId: '',
          searchText: '',
          rotationClass: '',
        },
        onFilterChange: () => {},
        onRefresh: () => {},
        onOpenPromotion: () => {},
        categories: [],
        onPreviousPage: () => {},
        onNextPage: () => {},
      })
    );

    assert.match(html, /Salud de inventario/);
    assert.match(html, /CAPITAL INMOVILIZADO/);
    assert.match(html, /Queso añejo/);
    assert.match(html, /ih-badge-dead/);
    assert.match(html, /Lacteos/);
    assert.match(html, /Aplicar Promocion \/ Descuento/);
  });
});
