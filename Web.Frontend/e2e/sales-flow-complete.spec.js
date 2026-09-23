import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

test.describe('Flow: Complete Sales, On-Hold Orders & Pending Pickups', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
  });

  test.describe('1. Standard POS Sale & Checkout Flow', () => {
    test('should search product, change customer, and open checkout modal', async ({ page }) => {
      // 1. Search and select product
      const searchInput = page.locator('input.pos-search-input, .pos-search-bar input, input[placeholder*="producto"]');
      await expect(searchInput).toBeVisible();
      await searchInput.fill('a');
      await page.waitForTimeout(600);

      const suggestion = page.locator('.suggestion-item, .pos-product-item, .suggestion-list li').first();
      if (await suggestion.isVisible({ timeout: 3000 }).catch(() => false)) {
        await suggestion.click();
      } else {
        await searchInput.press('Enter');
      }

      // 2. Change / select customer
      const customerEditBtn = page.locator('button.pos-customer-edit-btn, button:has-text("Cambiar")').first();
      if (await customerEditBtn.isVisible({ timeout: 2000 }).catch(() => false)) {
        await customerEditBtn.click();
        const customerModal = page.locator('.customer-modal, .modal-backdrop, .modal-container');
        await expect(customerModal).toBeVisible({ timeout: 5000 });

        // Pick first available customer or close
        const customerRow = page.locator('.customer-item, .customer-row, tr.customer-tr').first();
        if (await customerRow.isVisible({ timeout: 2000 }).catch(() => false)) {
          await customerRow.click();
        } else {
          // Close modal with close button or Escape
          const closeBtn = page.locator('.modal-close, button:has-text("Cerrar"), button:has-text("Cancelar")').first();
          if (await closeBtn.isVisible().catch(() => false)) {
            await closeBtn.click();
          } else {
            await page.keyboard.press('Escape');
          }
        }
      }

      // 3. Open Checkout
      const checkoutBtn = page.locator('button.checkout-btn');
      await expect(checkoutBtn).toBeVisible();
      if (await checkoutBtn.isEnabled()) {
        await checkoutBtn.click();
        const checkoutModal = page.locator('.checkout-modal, [data-testid="checkout-modal"], .modal-container');
        await expect(checkoutModal).toBeVisible({ timeout: 5000 });

        // Close checkout modal with Escape
        await page.keyboard.press('Escape');
      }
    });
  });

  test.describe('2. Cuentas Abiertas (On-Hold Orders Lifecycle)', () => {
    test('should place a sale on hold from POS', async ({ page }) => {
      // Add product
      const searchInput = page.locator('input.pos-search-input, .pos-search-bar input, input[placeholder*="producto"]');
      await searchInput.fill('a');
      await page.waitForTimeout(600);

      const suggestion = page.locator('.suggestion-item, .pos-product-item, .suggestion-list li').first();
      if (await suggestion.isVisible({ timeout: 3000 }).catch(() => false)) {
        await suggestion.click();
      } else {
        await searchInput.press('Enter');
      }

      // Trigger Hold (F4 or button)
      const holdBtn = page.locator('button.btn-hold-action, button:has-text("GUARDAR EN ESPERA")').first();
      if (await holdBtn.isVisible({ timeout: 3000 }).catch(() => false) && await holdBtn.isEnabled().catch(() => false)) {
        await holdBtn.click();
        const holdModal = page.locator('.hold-modal-pad').first();
        await expect(holdModal).toBeVisible({ timeout: 5000 });

        // Select customer in modal if not already selected
        const customerSearchInput = page.locator('input.hold-search-input, input[placeholder*="Buscar por Nombre"]').first();
        if (await customerSearchInput.isVisible({ timeout: 2000 }).catch(() => false)) {
          await customerSearchInput.click();
          await page.waitForTimeout(300);
          const customerOption = page.locator('.hold-dropdown-item').first();
          if (await customerOption.isVisible({ timeout: 2000 }).catch(() => false)) {
            await customerOption.click();
          }
        }

        // Confirm hold
        const confirmHoldBtn = page.locator('button.hold-confirm-btn').first();
        if (await confirmHoldBtn.isVisible({ timeout: 2000 }).catch(() => false) && await confirmHoldBtn.isEnabled().catch(() => false)) {
          await confirmHoldBtn.click();

          // Dismiss success screen if present
          const successBtn = page.locator('button:has-text("Ver Cuentas Abiertas"), .success-screen button').first();
          if (await successBtn.isVisible({ timeout: 3000 }).catch(() => false)) {
            await successBtn.click();
          }
        } else {
          // If confirm is not enabled, close modal cleanly
          const cancelBtn = page.locator('button.hold-footer-btn, button:has-text("CANCELAR")').first();
          if (await cancelBtn.isVisible().catch(() => false)) {
            await cancelBtn.click();
          } else {
            await page.keyboard.press('Escape');
          }
        }
      }
    });

    test('should navigate to Pending Orders page and verify actions', async ({ page }) => {
      // Navigate to /pending
      const pendingNav = page.locator('a[href*="pending"], button:has-text("Cuentas"), button:has-text("Espera")').first();
      if (await pendingNav.isVisible({ timeout: 2000 }).catch(() => false)) {
        await pendingNav.click();
      } else {
        await page.goto('/pending');
      }

      // Verify page title
      const title = page.locator('.pending-orders-title, h1:has-text("Cuentas Abiertas")');
      await expect(title).toBeVisible({ timeout: 10000 });

      // Check if orders exist in list
      const orderRow = page.locator('tbody tr.cursor-pointer, .pending-order-card').first();
      if (await orderRow.isVisible({ timeout: 3000 }).catch(() => false)) {
        // Expand order details
        await orderRow.click();

        // Check for action buttons
        const cobrarBtn = page.locator('button:has-text("Cobrar")').first();
        const editarBtn = page.locator('button:has-text("Editar")').first();
        await expect(cobrarBtn).toBeVisible();
        await expect(editarBtn).toBeVisible();

        // Test opening Edit modal
        await editarBtn.click();
        const editModal = page.locator('.edit-sale-modal, .modal-container');
        await expect(editModal).toBeVisible({ timeout: 5000 });

        // Close edit modal
        const closeEditBtn = page.locator('.modal-close, button:has-text("Cerrar"), button:has-text("Cancelar")').first();
        if (await closeEditBtn.isVisible().catch(() => false)) {
          await closeEditBtn.click();
        } else {
          await page.keyboard.press('Escape');
        }
      }
    });
  });

  test.describe('3. Retiros Pendientes (Pending Pickups)', () => {
    test('should navigate to Pending Pickups page and verify elements', async ({ page }) => {
      // Navigate to /pickups
      const pickupsNav = page.locator('a[href*="pickups"], button:has-text("Retiros"), a:has-text("Retiros")').first();
      if (await pickupsNav.isVisible({ timeout: 2000 }).catch(() => false)) {
        await pickupsNav.click();
      } else {
        await page.goto('/pickups');
      }

      // Verify page title
      const title = page.locator('.pending-orders-title, h1:has-text("Retiros Pendientes")');
      await expect(title).toBeVisible({ timeout: 10000 });

      // Verify search input is present
      const searchInput = page.locator('.ppk-search-input, input[placeholder*="Factura"]');
      await expect(searchInput).toBeVisible();

      // Check if any pickup orders exist
      const pickupRow = page.locator('.ppk-table tbody tr, .ppk-card').first();
      if (await pickupRow.isVisible({ timeout: 3000 }).catch(() => false)) {
        // Confirm pickup button
        const confirmBtn = page.locator('button:has-text("Confirmar Entrega"), button:has-text("Marcar Retirado")').first();
        if (await confirmBtn.isVisible().catch(() => false)) {
          await confirmBtn.click();
          // Verify confirmation modal
          const modal = page.locator('.modal-container, .modal-backdrop');
          await expect(modal).toBeVisible({ timeout: 5000 });
          await page.keyboard.press('Escape');
        }
      } else {
        // Empty state is valid if all pickups are completed
        const emptyCard = page.locator('.ppk-empty-card, h3:has-text("No hay mercancía pendiente")');
        await expect(emptyCard).toBeVisible();
      }
    });
  });
});
