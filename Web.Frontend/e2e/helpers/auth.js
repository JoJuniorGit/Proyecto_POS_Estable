import { expect } from '@playwright/test';
import { setupMockApi } from './mockApi';

/**
 * Logs in as admin handling development seed credentials and mandatory password change.
 * @param {import('@playwright/test').Page} page
 */
export async function loginAsAdmin(page) {
  // Ensure network routes are intercepted for deterministic E2E execution
  await setupMockApi(page);

  await page.goto('/');

  // If already logged in, return
  const posIndicator = page.locator('.pos-page').first();
  if (await posIndicator.isVisible({ timeout: 1500 }).catch(() => false)) {
    return;
  }

  const usernameInput = page.locator('input[placeholder*="Usuario"]');
  const passwordInput = page.locator('input[placeholder*="Contraseña"]');
  const loginButton = page.locator('button.login-btn');

  if (await usernameInput.isVisible({ timeout: 3000 }).catch(() => false)) {
    await usernameInput.fill('admin');
    await passwordInput.fill('Admin123*');
    await loginButton.click();

    // Check if error "Credenciales inválidas" appeared
    const errorLocator = page.locator('.login-error');
    const isError = await errorLocator.isVisible({ timeout: 1500 }).catch(() => false);

    if (isError) {
      await usernameInput.fill('admin');
      await passwordInput.fill('DevAdmin!2026');
      await loginButton.click();
    }

    // Check if "Actualizar Contraseña" (MustChangePassword) is displayed
    const updateHeader = page.locator('h1.login-title:has-text("Actualizar Contraseña")');
    if (await updateHeader.isVisible({ timeout: 2000 }).catch(() => false)) {
      const newPassInput = page.locator('input[placeholder*="Nueva Contraseña"]');
      const confirmPassInput = page.locator('input[placeholder*="Confirmar Contraseña"]');
      const saveButton = page.locator('button.login-btn');

      await newPassInput.fill('Admin123*');
      await confirmPassInput.fill('Admin123*');
      await saveButton.click();
    }

    // Wait for POS view
    await expect(posIndicator).toBeVisible({ timeout: 10000 });
  }
}
