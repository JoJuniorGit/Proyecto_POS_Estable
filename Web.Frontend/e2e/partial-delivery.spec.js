import { test, expect } from '@playwright/test';
import { loginAsAdmin } from './helpers/auth';

function createPickup(firstItemPendingQuantity = 3) {
  return {
    saleId: 301,
    invoiceNumber: 'FAC-00301',
    date: '2026-10-03T12:00:00.000Z',
    customerName: 'Maria Rodriguez',
    customerCedula: 'V-87654321',
    totalBsS: 364,
    totalUSD: 8,
    deliveryStatus: 'PartiallyDelivered',
    items: [
      {
        id: 701,
        saleItemId: 701,
        productId: 1,
        productName: 'Harina PAN 1Kg',
        quantity: 5,
        totalQuantity: 5,
        deliveredQuantity: 5 - firstItemPendingQuantity,
        pendingQuantity: firstItemPendingQuantity,
        unitPriceUSD: 1,
        unitPriceBsS: 45.5,
        subtotalUSD: 5,
        subtotalBsS: 227.5,
      },
      {
        id: 702,
        saleItemId: 702,
        productId: 2,
        productName: 'Arroz 1Kg',
        quantity: 3,
        totalQuantity: 3,
        deliveredQuantity: 1,
        pendingQuantity: 2,
        unitPriceUSD: 1,
        unitPriceBsS: 45.5,
        subtotalUSD: 3,
        subtotalBsS: 136.5,
      },
    ],
  };
}

async function mockPendingPickups(page, pickupForRequest) {
  let requestCount = 0;
  await page.route('**/api/sales/pending-pickups*', async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();

    requestCount += 1;
    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { 'x-total-count': '1' },
      body: JSON.stringify([pickupForRequest(requestCount)]),
    });
  });

  return () => requestCount;
}

async function navigateToPendingPickups(page) {
  const pickupsNav = page.locator('a[href*="pickups"], button:has-text("Retiros"), a:has-text("Retiros")').first();
  if (await pickupsNav.isVisible({ timeout: 2000 }).catch(() => false)) {
    await pickupsNav.click();
  } else {
    await page.goto('/pickups');
  }
}

test.describe('Partial custody delivery', () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
  });

  test('prefills and submits all pending quantities from Retiro Completo', async ({ page }) => {
    await mockPendingPickups(page, () => createPickup());

    let postedBody;
    await page.route('**/api/sales/301/deliveries', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();

      postedBody = JSON.parse(route.request().postData() || '{}');
      return route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          deliveryId: 900,
          saleId: 301,
          invoiceNumber: 'FAC-00301',
          deliveryStatus: 'Delivered',
        }),
      });
    });

    await navigateToPendingPickups(page);
    await page.getByRole('button', { name: 'Retiro Completo' }).click();

    const modal = page.getByRole('dialog', { name: 'Despachar mercancía' });
    await expect(modal).toBeVisible();
    const quantities = modal.getByLabel('Cantidad a retirar hoy');
    await expect(quantities).toHaveCount(2);
    await expect(quantities.nth(0)).toHaveValue('3');
    await expect(quantities.nth(1)).toHaveValue('2');

    const confirmButton = modal.getByRole('button', { name: 'Confirmar Retiro' });
    await expect(confirmButton).toBeEnabled();
    await confirmButton.click();

    expect(postedBody.items).toEqual([
      { saleItemId: 701, quantity: 3 },
      { saleItemId: 702, quantity: 2 },
    ]);
    await expect(modal).toBeHidden();
    await expect(page.getByRole('button', { name: 'Imprimir Nota de Despacho' })).toBeVisible();
  });

  test('shows progress, validates quantities, and submits a normalized delivery', async ({ page }) => {
    await mockPendingPickups(page, () => createPickup());

    let postedBody;
    await page.route('**/api/sales/301/deliveries', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();

      postedBody = JSON.parse(route.request().postData() || '{}');
      return route.fulfill({
        status: 201,
        contentType: 'application/json',
        body: JSON.stringify({
          deliveryId: 901,
          saleId: 301,
          invoiceNumber: 'FAC-00301',
          deliveryStatus: 'PartiallyDelivered',
        }),
      });
    });

    await navigateToPendingPickups(page);
    await expect(page.getByRole('heading', { name: /Retiros Pendientes/ })).toBeVisible();
    await expect(page.getByText('Entrega Parcial', { exact: true })).toBeVisible();
    await expect(page.locator('.ppk-progress-label')).toHaveText('Retirado: 3/8 artículos');

    await page.getByRole('button', { name: 'Confirmar Retiro' }).click();
    const modal = page.getByRole('dialog', { name: 'Despachar mercancía' });
    await expect(modal).toBeVisible();

    const quantities = modal.getByLabel('Cantidad a retirar hoy');
    await expect(quantities).toHaveCount(2);
    await quantities.nth(0).fill('4');
    await expect(quantities.nth(0)).toHaveValue('3');
    await expect(modal.getByText('La cantidad debe estar entre 0 y 3.', { exact: true })).toBeVisible();

    const confirmButton = modal.getByRole('button', { name: 'Confirmar Retiro' });
    await expect(confirmButton).toBeDisabled();
    await quantities.nth(0).fill('1.23456');
    await expect(quantities.nth(0)).toHaveValue('1.235');
    await quantities.nth(1).fill('0.3756');
    await expect(quantities.nth(1)).toHaveValue('0.376');
    await expect(confirmButton).toBeEnabled();

    await confirmButton.click();

    expect(postedBody.items).toEqual([
      { saleItemId: 701, quantity: 1.235 },
      { saleItemId: 702, quantity: 0.376 },
    ]);
    await expect(modal).toBeHidden();
    await expect(page.getByRole('status').getByText(/Retiro registrado con éxito/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Imprimir Nota de Despacho' })).toBeVisible();
  });

  test('keeps modal data on 409 reload and revalidates against the refreshed pending quantity', async ({ page }) => {
    let conflictSubmitted = false;
    let postConflictRefreshCount = 0;
    const getPendingRequestCount = await mockPendingPickups(page, () => {
      if (!conflictSubmitted) return createPickup(3);

      postConflictRefreshCount += 1;
      return createPickup(postConflictRefreshCount === 1 ? 2 : 3);
    });

    await page.route('**/api/sales/301/deliveries', async (route) => {
      if (route.request().method() !== 'POST') return route.fallback();

      conflictSubmitted = true;
      return route.fulfill({
        status: 409,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'La entrega cambió. Revise las cantidades e intente de nuevo.' }),
      });
    });

    await navigateToPendingPickups(page);
    await expect(page.getByRole('heading', { name: /Retiros Pendientes/ })).toBeVisible();
    await page.getByRole('button', { name: 'Confirmar Retiro' }).click();

    const modal = page.getByRole('dialog', { name: 'Despachar mercancía' });
    const quantities = modal.getByLabel('Cantidad a retirar hoy');
    await quantities.nth(0).fill('2.5');
    await quantities.nth(1).fill('0.75');
    await modal.getByRole('button', { name: 'Confirmar Retiro' }).click();

    await expect(modal).toBeVisible();
    await expect(modal.getByText('La entrega cambió. Revise las cantidades e intente de nuevo.', { exact: true })).toBeVisible();
    await expect(quantities.nth(0)).toHaveValue('2');
    await expect(quantities.nth(0)).toHaveAttribute('aria-invalid', 'true');
    await expect(modal.getByText('La cantidad debe estar entre 0 y 2.', { exact: true })).toBeVisible();
    await expect(quantities.nth(1)).toHaveValue('0.75');
    expect(postConflictRefreshCount).toBe(1);
    expect(getPendingRequestCount()).toBeGreaterThan(1);

    await page.evaluate(() => window.dispatchEvent(new Event('pendingPickupsUpdated')));

    await expect.poll(() => postConflictRefreshCount).toBe(2);
    await expect(quantities.nth(0)).toHaveValue('2');
    await expect(quantities.nth(0)).toHaveAttribute('aria-invalid', 'false');
    await expect(modal.getByText('La cantidad debe estar entre 0 y 2.', { exact: true })).toHaveCount(0);
    await expect(modal.getByRole('button', { name: 'Confirmar Retiro' })).toBeEnabled();
    await expect(modal).toBeVisible();
  });
});
