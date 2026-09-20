import { test, expect } from '@playwright/test';

test.describe('Flow 1: Authentication & Session', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('should display login form with required fields', async ({ page }) => {
    await expect(page.locator('h1.login-title')).toContainText('Inicio de Sesión POS');
    await expect(page.locator('input[placeholder*="Usuario"]')).toBeVisible();
    await expect(page.locator('input[placeholder*="Contraseña"]')).toBeVisible();
    await expect(page.locator('button.login-btn')).toBeVisible();
  });

  test('should show error when submitting invalid credentials', async ({ page }) => {
    await page.locator('input[placeholder*="Usuario"]').fill('invalid_user');
    await page.locator('input[placeholder*="Contraseña"]').fill('wrong_password');
    await page.locator('button.login-btn').click();

    const errorMessage = page.locator('.login-error');
    await expect(errorMessage).toBeVisible();
  });

  test('should log in successfully with valid credentials and navigate to POS', async ({ page }) => {
    // Fill credentials
    await page.locator('input[placeholder*="Usuario"]').fill('admin');
    await page.locator('input[placeholder*="Contraseña"]').fill('Admin123*');
    await page.locator('button.login-btn').click();

    // Check redirection or view render
    await expect(page.locator('.pos-page, .pos-layout, .pos-customer-bar')).toBeVisible({ timeout: 10000 });
  });
});
