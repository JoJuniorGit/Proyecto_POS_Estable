import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

test.describe('Flow 6: Product Catalog & Inventory Lookup', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
  });

  test('should navigate to Catalog page and search products', async ({ page }) => {
    // Navigate to /catalog
    const catalogNav = page.locator('a[href*="catalog"], button:has-text("Catálogo"), a:has-text("Catálogo")').first();
    if (await catalogNav.isVisible({ timeout: 2000 }).catch(() => false)) {
      await catalogNav.click();
    } else {
      await page.goto('/catalog');
    }

    // Verify Catalog view container or title
    const catalogContainer = page.locator('.catalog-page, .catalog-container, h1:has-text("Catálogo"), h2:has-text("Catálogo")').first();
    await expect(catalogContainer).toBeVisible({ timeout: 10000 });

    // Search for product
    const searchInput = page.locator('input[placeholder*="Buscar"], input.catalog-search, input[type="search"]').first();
    if (await searchInput.isVisible({ timeout: 3000 }).catch(() => false)) {
      await searchInput.fill('Harina');
      await page.waitForTimeout(500);

      // Verify product card or row
      const productItem = page.locator('.catalog-item, .catalog-card, tr.product-row, .product-card').first();
      await expect(productItem).toBeVisible({ timeout: 5000 });
    }
  });
});
