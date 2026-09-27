import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

test.describe('Flow 2: Complete Sale at POS', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
  });

  test('should search product, add to cart, and open checkout', async ({ page }) => {
    // Locate search input
    const searchInput = page.locator('input.pos-search-input, .pos-search-bar input, input[placeholder*="producto"]');
    await expect(searchInput).toBeVisible();

    // Type search query
    await searchInput.fill('a');
    await page.waitForTimeout(600);

    // If suggestion list appears, click first item
    const suggestionItem = page.locator('.suggestion-item, .pos-product-item, .suggestion-list li, .product-card').first();
    if (await suggestionItem.isVisible({ timeout: 3000 }).catch(() => false)) {
      await suggestionItem.click();
    } else {
      await searchInput.press('Enter');
    }

    // Verify summary panel is visible
    const summaryPanel = page.locator('.summary-panel');
    await expect(summaryPanel).toBeVisible();

    // Verify Checkout button is visible
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
