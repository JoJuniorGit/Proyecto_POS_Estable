/**
 * Sets up Playwright network route mocks for all backend API endpoints.
 * Ensures the Web POS client can be tested end-to-end deterministically
 * without requiring an active PostgreSQL database or backend instance.
 * @param {import('@playwright/test').Page} page
 */
export async function setupMockApi(page) {
  // 1. Auth & Session
  await page.route('**/api/auth/login', async (route) => {
    const request = route.request();
    const postData = JSON.parse(request.postData() || '{}');
    if (postData.cedula === 'invalid_user' || postData.password === 'wrong_password') {
      return route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'Credenciales inválidas.' }),
      });
    }

    return route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        user: {
          id: 1,
          name: 'Administrador',
          username: 'admin',
          role: 'Admin',
          cedula: '12345678',
        },
        token: 'mock-jwt-token-e2e',
      }),
    });
  });

  await page.route('**/api/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 1,
        name: 'Administrador',
        username: 'admin',
        role: 'Admin',
      }),
    })
  );

  // 2. Exchange Rate
  await page.route('**/api/exchange-rate/today', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ Value: 45.5, value: 45.5 }),
    })
  );

  // 3. Products
  await page.route('**/api/products/suggestions*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        {
          id: 1,
          name: 'Harina PAN 1Kg',
          sku: 'HAR-001',
          priceUSD: 1.5,
          costPriceUSD: 1.0,
          profitMargin: 50.0,
          stockQuantity: 100,
          isActive: true,
        },
      ]),
    })
  );

  await page.route('**/api/products/quick-check/*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 1,
        name: 'Harina PAN 1Kg',
        sku: 'HAR-001',
        priceUSD: 1.5,
        stockQuantity: 100,
        isActive: true,
      }),
    })
  );

  // 4. POS Sale lifecycle
  await page.route('**/api/sales/start*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 101,
        status: 'Draft',
        customerName: 'Consumidor Final',
        items: [],
        totalAmountUSD: 0,
        totalAmountLocal: 0,
        priceListType: 'Retail',
        exchangeRate: 45.5,
      }),
    })
  );

  await page.route('**/api/sales/*/items*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 101,
        status: 'Draft',
        customerName: 'Consumidor Final',
        items: [
          {
            id: 1,
            productId: 1,
            productName: 'Harina PAN 1Kg',
            quantity: 1,
            unitPriceUSD: 1.5,
            unitPriceBsS: 68.25,
            subtotalUSD: 1.5,
            subtotalBsS: 68.25,
          },
        ],
        totalAmountUSD: 1.5,
        totalAmountLocal: 68.25,
        priceListType: 'Retail',
        exchangeRate: 45.5,
      }),
    })
  );

  await page.route('**/api/sales/*/customer', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ success: true }),
    })
  );

  await page.route('**/api/sales/*/hold', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ id: 101, status: 'OnHold' }),
    })
  );

  await page.route('**/api/sales/*/complete', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(1001),
    })
  );

  // 5. On-Hold Orders (Pending)
  await page.route('**/api/sales/pending*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { 'x-total-count': '1' },
      body: JSON.stringify([
        {
          id: 201,
          date: new Date().toISOString(),
          customerName: 'Juan Perez',
          customerCedula: 'V-12345678',
          totalBsS: 136.5,
          totalUSD: 3.0,
          totalPaidUSD: 0,
          payments: [],
          items: [
            {
              id: 1,
              productId: 1,
              productName: 'Harina PAN 1Kg',
              quantity: 2,
              unitPriceUSD: 1.5,
              unitPriceBsS: 68.25,
              subtotalUSD: 3.0,
              subtotalBsS: 136.5,
            },
          ],
        },
      ]),
    })
  );

  await page.route('**/api/sales/*/claim', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ success: true, claimedBy: 1 }),
    })
  );

  await page.route('**/api/sales/*/release', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ success: true }),
    })
  );

  await page.route('**/api/sales/*/cancel', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ status: 'Cancelled' }),
    })
  );

  // 6. Retiros Pendientes (Pending Pickups)
  await page.route('**/api/sales/pending-pickups*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { 'x-total-count': '1' },
      body: JSON.stringify([
        {
          saleId: 301,
          invoiceNumber: 'FAC-00301',
          date: new Date().toISOString(),
          customerName: 'Maria Rodriguez',
          customerCedula: 'V-87654321',
          totalBsS: 68.25,
          totalUSD: 1.5,
          items: [
            {
              id: 1,
              productId: 1,
              productName: 'Harina PAN 1Kg',
              quantity: 1,
              unitPriceUSD: 1.5,
              unitPriceBsS: 68.25,
              subtotalUSD: 1.5,
              subtotalBsS: 68.25,
            },
          ],
        },
      ]),
    })
  );

  await page.route('**/api/sales/*/confirm-pickup', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ success: true }),
    })
  );

  // 7. Cash Drawer & Register
  await page.route('**/api/cashdrawer/active-session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: 1,
        openingBalanceLocal: 500.0,
        transactions: [
          {
            id: 1,
            type: 0,
            source: 1,
            amountLocal: 500.0,
            isPhysicalCash: true,
            transactionTime: new Date().toISOString(),
          },
        ],
      }),
    })
  );

  await page.route('**/api/cashdrawer/history*', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([]),
    })
  );

  // 8. Payment methods & Customers
  await page.route('**/api/paymentmethods**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 1, name: 'Efectivo USD', code: 'USD_CASH', currency: 'USD', isActive: true },
        { id: 2, name: 'Efectivo Bs.S', code: 'VES_CASH', currency: 'Bs.S', isActive: true },
      ]),
    })
  );

  await page.route('**/api/customers**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 1, name: 'Cliente Frecuente', cedulaOrRif: 'V-99999999' },
      ]),
    })
  );

  await page.route('**/api/sales/customers**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: 1, name: 'Cliente Frecuente', cedulaOrRif: 'V-99999999' },
      ]),
    })
  );
}
