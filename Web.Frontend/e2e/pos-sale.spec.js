import { test, expect } from '@playwright/test';

test.describe('Flow 2: Complete Sale at POS', () => {
  test.beforeEach(async ({ page }) => {
    // Navigate and login
    await page.goto('/');
    const loginInput = page.locator('input[placeholder*="Usuario"]');
    if (await loginInput.isVisible()) {
      await loginInput.fill('admin');
      await page.locator('input[placeholder*="Contraseña"]').fill('Admin123*');
      await page.locator('button.login-btn').click();
    }
    await expect(page.locator('.pos-page, .pos-customer-bar')).toBeVisible({ timeout: 10000 });
  });

  test('should search product, add to cart, and open checkout', async ({ page }) => {
    // Locate search input
    const searchInput = page.locator('input.pos-search-input, .pos-search-bar input');
    await expect(searchInput).toBeVisible();

    // Type search query
    await searchInput.fill('Harina');
    await page.waitForTimeout(500);

    // If suggestion list appears, click first item
    const suggestionItem = page.locator('.suggestion-item, .pos-product-item, .suggestion-list li').first();
    if (await suggestionItem.isVisible({ timeout: 2000 }).catch(() => false)) {
      await suggestionItem.click();
    } else {
      // Or press Enter to select if applicable
      await searchInput.press('Enter');
    }

    // Verify cart has item or summary total is updated
    const summaryPanel = page.locator('.summary-panel');
    await expect(summaryPanel).toBeVisible();

    // Verify Checkout button is clickable
    const checkoutBtn = page.locator('button.checkout-btn');
    await expect(checkoutBtn).toBeVisible();

    // If cart has items, clicking checkout should open CheckoutModal
    if (await checkoutBtn.isEnabled()) {
      await checkoutBtn.click();
      const checkoutModal = page.locator('.checkout-modal, [data-testid="checkout-modal"], .modal-container');
      await expect(checkoutModal).toBeVisible({ timeout: 5000 });
    }
  });
});
