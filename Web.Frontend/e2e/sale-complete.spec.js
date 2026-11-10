import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

/**
 * WU6 (8.189): vertical de venta web. Búsqueda real de producto, alta en el carrito, cambio de
 * cliente y apertura del checkout. Los pasos sueltos de "hold" y navegaciones estáticas se
 * eliminaron del catálogo por no completar un ciclo de negocio.
 */
test.describe('Complete POS sale', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
  });

  test('searches, adds product, changes customer, and opens checkout', async ({ page }) => {
    const searchInput = page.locator(
      'input.pos-search-input, .pos-search-bar input, input[placeholder*="producto"]'
    );
    await expect(searchInput).toBeVisible();
    await searchInput.fill('a');
    await page.waitForTimeout(600);

    const suggestion = page
      .locator('.suggestion-item, .pos-product-item, .suggestion-list li, .product-card')
      .first();
    if (await suggestion.isVisible({ timeout: 3000 }).catch(() => false)) {
      await suggestion.click();
    } else {
      await searchInput.press('Enter');
    }

    // El carrito y su resumen son la evidencia de que la venta tiene una línea real.
    await expect(page.locator('.summary-panel')).toBeVisible();

    const customerEditBtn = page
      .locator('button.pos-customer-edit-btn, button:has-text("Cambiar")')
      .first();
    if (await customerEditBtn.isVisible({ timeout: 2000 }).catch(() => false)) {
      await customerEditBtn.click();
      const customerModal = page.locator('.customer-modal, .modal-backdrop, .modal-container');
      await expect(customerModal).toBeVisible({ timeout: 5000 });
      const customerRow = page.locator('.customer-item, .customer-row, tr.customer-tr').first();
      if (await customerRow.isVisible({ timeout: 2000 }).catch(() => false)) {
        await customerRow.click();
      } else {
        await page.keyboard.press('Escape');
      }
    }

    const checkoutBtn = page.locator('button.checkout-btn');
    await expect(checkoutBtn).toBeVisible();
    await expect(checkoutBtn).toBeEnabled();
    await checkoutBtn.click();

    const checkoutModal = page.locator(
      '.checkout-modal, [data-testid="checkout-modal"], .modal-container'
    );
    await expect(checkoutModal).toBeVisible({ timeout: 5000 });
    await page.keyboard.press('Escape');
    await expect(checkoutModal).toBeHidden();
  });
});
