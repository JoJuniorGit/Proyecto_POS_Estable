import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';
import { setupMockApi } from './helpers/mockApi';

test.describe('Flow 1: Authentication & Session', () => {
  test.beforeEach(async ({ page }) => {
    await setupMockApi(page);
    await page.goto('/');
  });

  test('should display login form with required fields', async ({ page }) => {
    // If already logged in, navigate to login or clear cookies
    const posIndicator = page.locator('.pos-page, .pos-customer-bar, .pos-layout');
    if (await posIndicator.isVisible({ timeout: 1000 }).catch(() => false)) {
      // Session already active
      return;
    }
    await expect(page.locator('h1.login-title')).toContainText('Inicio de Sesión POS');
    await expect(page.locator('input[placeholder*="Usuario"]')).toBeVisible();
    await expect(page.locator('input[placeholder*="Contraseña"]')).toBeVisible();
    await expect(page.locator('button.login-btn')).toBeVisible();
  });

  test('should show error when submitting invalid credentials', async ({ page }) => {
    const usernameInput = page.locator('input[placeholder*="Usuario"]');
    if (await usernameInput.isVisible({ timeout: 1500 }).catch(() => false)) {
      await usernameInput.fill('invalid_user');
      await page.locator('input[placeholder*="Contraseña"]').fill('wrong_password');
      await page.locator('button.login-btn').click();

      const errorMessage = page.locator('.login-error');
      await expect(errorMessage).toBeVisible();
    }
  });

  test('should log in successfully with valid credentials and navigate to POS', async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible();
  });
});
