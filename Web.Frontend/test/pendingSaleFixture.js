export function buildPendingSale(overrides) {
  return {
    id: 1,
    date: '2026-09-10T14:30:00Z',
    customerName: 'Juan Pérez',
    customerCedula: 'V-12345678',
    customer: { name: 'Juan Pérez', cedulaOrRif: 'V-12345678' },
    items: [
      { id: 1, productName: 'Café', quantity: 2, unitPriceBsS: 840.42, subtotalBsS: 1680.84 },
    ],
    payments: [
      { id: 1, paymentMethodName: 'Efectivo Bs.S', amountBsS: 500, amount: 0.59, exchangeRate: 847.44, createdAt: '2026-09-10T15:00:00Z' },
    ],
    totalBsS: 1680.84,
    totalUSD: 2,
    totalPaidUSD: 0.59,
    remainingBalanceUSD: 1.41,
    claimedByUserId: null,
    claimedByUserName: null,
    claimAction: null,
    ...overrides,
  };
}
