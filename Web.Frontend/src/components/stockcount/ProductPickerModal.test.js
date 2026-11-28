import { describe, it } from 'node:test';
import assert from 'node:assert';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { renderToString } from 'react-dom/server';
import React from 'react';
import ProductPickerModal, {
  MAX_SCOPE_PRODUCTS,
  formatScopeCounter,
  isCountablePickerProduct,
  toggleScopeProduct,
} from './ProductPickerModal.jsx';

const baseProduct = (overrides = {}) => ({
  id: 10,
  sku: 'SKU-1',
  name: 'Harina',
  isActive: true,
  isDeleted: false,
  isCashAdvance: false,
  isGroupHeader: false,
  isStockShared: false,
  parentProductId: null,
  parentIsStockShared: false,
  ...overrides,
});

describe('ProductPickerModal — WU3 product scope', () => {
  it('1. only countable, active products can be selected', () => {
    assert.strictEqual(isCountablePickerProduct(baseProduct()), true);
    assert.strictEqual(isCountablePickerProduct(baseProduct({ isActive: false })), false);
    assert.strictEqual(isCountablePickerProduct(baseProduct({ isDeleted: true })), false);
    assert.strictEqual(isCountablePickerProduct(baseProduct({ isCashAdvance: true })), false);
    assert.strictEqual(isCountablePickerProduct(baseProduct({ isGroupHeader: true, isStockShared: false })), false);
    assert.strictEqual(
      isCountablePickerProduct(baseProduct({ parentProductId: 1, parentIsStockShared: true })),
      false
    );
    assert.strictEqual(isCountablePickerProduct(null), false);
  });

  it('2. toggles selection, removes duplicates and enforces the max', () => {
    const product = baseProduct();
    const first = toggleScopeProduct([], product);
    assert.deepStrictEqual(first.selected, [{ id: 10, sku: 'SKU-1', name: 'Harina' }]);
    assert.strictEqual(first.error, null);

    const removed = toggleScopeProduct(first.selected, product);
    assert.deepStrictEqual(removed.selected, []);

    const atMax = Array.from({ length: 2 }, (_, index) => ({
      id: index + 1,
      sku: `SKU-${index + 1}`,
      name: `Producto ${index + 1}`,
    }));
    const blocked = toggleScopeProduct(atMax, product, 2);
    assert.strictEqual(blocked.selected.length, 2);
    assert.match(blocked.error, /Máximo 2/);
    assert.strictEqual(formatScopeCounter(blocked.selected.length, 2), '2 / 2');
  });

  it('3. renders the counter and selected chips without stock or cost fields', () => {
    const html = renderToString(
      <ProductPickerModal
        categories={[{ id: 1, name: 'Alimentos', path: 'Alimentos' }]}
        selectedProducts={[{ id: 10, sku: 'SKU-1', name: 'Harina' }]}
        onClose={() => {}}
        onConfirm={() => {}}
      />
    );

    assert.match(html, /Seleccionar productos/);
    assert.match(html, /1 \/ 1000/);
    assert.match(html, /Harina/);
    assert.match(html, /SKU-1/);
    assert.match(html, /pagination-container/);
    assert.match(html, /Página/);
    assert.match(html, /Ir a:/);
    assert.doesNotMatch(html, /stockQuantity|costPriceUSD|priceUSD/i);
    assert.strictEqual(MAX_SCOPE_PRODUCTS, 1000);
  });

  it('4. uses the theme surface and soft theme-aware row states', () => {
    const cssPath = join(dirname(fileURLToPath(import.meta.url)), 'ProductPickerModal.css');
    const css = readFileSync(cssPath, 'utf8');

    assert.match(css, /\.pp-results,[\s\S]*?\.pp-selected\s*\{[\s\S]*?background:\s*var\(--bg-surface/);
    assert.doesNotMatch(css, /background:\s*var\(--bg-card/);
    assert.match(css, /\.pp-row:hover\s*\{[\s\S]*?background:\s*#F8FAFC/);
    assert.match(css, /\.pp-row\.is-selected\s*\{[\s\S]*?background:\s*#F5F3FF/);
    assert.match(css, /\[data-theme='dark'\] \.pp-row:hover\s*\{[\s\S]*?background:\s*rgba\(148, 163, 184, 0\.12\)/);
    assert.match(css, /\[data-theme='dark'\] \.pp-row\.is-selected/);
  });
});
