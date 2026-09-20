import { test, expect } from '@playwright/test';

test.describe('Flow 3: Cash Drawer Operations', () => {
  test.beforeEach(async ({ page }) => {
    // Navigate and login as admin
    await page.goto('/');
    const loginInput = page.locator('input[placeholder*="Usuario"]');
    if (await loginInput.isVisible()) {
      await loginInput.fill('admin');
      await page.locator('input[placeholder*="Contraseña"]').fill('Admin123*');
      await page.locator('button.login-btn').click();
    }
    await expect(page.locator('.pos-page, .pos-customer-bar')).toBeVisible({ timeout: 10000 });
  });

  test('should navigate to Register page and display session status', async ({ page }) => {
    // Navigate via navigation link/button to register page
    const registerNavBtn = page.locator('a[href*="register"], button:has-text("Caja"), button:has-text("Register")').first();
    if (await registerNavBtn.isVisible({ timeout: 2000 }).catch(() => false)) {
      await registerNavBtn.click();
    } else {
      await page.goto('/register');
    }

    // Verify register page elements
    const pageTitle = page.locator('.register-main-title, h2:has-text("Caja")');
    await expect(pageTitle).toBeVisible({ timeout: 5000 });

    // Verify presence of Cash In and Cash Out buttons
    const cashInBtn = page.locator('button:has-text("CASH IN")');
    await expect(cashInBtn).toBeVisible();

    const cashOutBtn = page.locator('button:has-text("CASH OUT")');
    await expect(cashOutBtn).toBeVisible();
  });
});
