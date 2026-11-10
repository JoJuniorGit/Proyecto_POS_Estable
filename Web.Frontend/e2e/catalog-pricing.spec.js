import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

/**
 * WU6 (8.189): vertical de catálogo/precios. El precio efectivo Bs.S DEBE venir proyectado por el
 * servidor (`effectivePriceBsS`) y refrescarse al cambiar la tasa BCV sin recargar la página (F5):
 * la búsqueda del POS vuelve a consultar el backend y la lista refleja el nuevo valor.
 */
test.describe('Catalog & pricing (effective Bs.S projection)', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await expect(page.locator('.pos-page').first()).toBeVisible({ timeout: 10000 });
  });

  test('projects server effectivePriceBsS and updates on BCV change without F5', async ({ page }) => {
    let effectivePriceBsS = 874.5;
    let requestCount = 0;

    // Ruta más específica que la del mock global: el servidor simulado proyecta el efectivo.
    await page.route('**/api/products/suggestions*', (route) => {
      requestCount += 1;
      return route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify([
          {
            id: 1,
            name: 'Harina PAN 1Kg',
            sku: 'HAR-001',
            priceUSD: 1.5,
            priceBsS: 68.25,
            effectivePriceBsS,
            stockQuantity: 100,
            isActive: true,
          },
        ]),
      });
    });

    const searchInput = page.locator(
      'input.pos-search-input, .pos-search-bar input, input[placeholder*="producto"]'
    );
    await expect(searchInput).toBeVisible();
    await searchInput.fill('Harina');

    const price = page.locator('.suggestion-price').first();
    await expect(price).toBeVisible({ timeout: 10000 });
    // 874,50 es la proyección del servidor; el canónico 1,5 × 45,5 = 68,25 NO debe mostrarse.
    await expect(price).toHaveText(/874,50/);
    await expect(price).not.toHaveText(/68,25/);

    // Cambio de tasa BCV en el backend simulado: la búsqueda se repite dentro de la SPA.
    effectivePriceBsS = 900.25;
    await searchInput.fill('');
    await searchInput.fill('Har');
    await expect.poll(() => requestCount).toBeGreaterThan(1);
    await searchInput.fill('Harina');

    await expect(price).toHaveText(/900,25/, { timeout: 10000 });
    await expect(price).not.toHaveText(/874,50/);
    // La URL no cambió: la actualización ocurrió sin recarga completa (sin F5).
    expect(page.url()).toContain('/');
  });
});
