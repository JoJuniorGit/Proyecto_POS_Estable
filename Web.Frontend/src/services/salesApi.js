import { api } from './api';

/**
 * Inicia una nueva venta pendiente en el backend.
 * @param {number} [cashierId]
 * @returns {Promise<SaleDto>}
 */
export async function startSale(cashierId) {
  const url = cashierId ? `/api/sales/start?cashierId=${cashierId}` : '/api/sales/start';
  return await api.post(url);
}

/**
 * Obtiene el estado actual de una venta.
 * @param {number} saleId
 */
export async function getSale(saleId) {
  return await api.get(`/api/sales/${saleId}`);
}

/**
 * Agrega un producto a la venta.
 * @param {number} saleId
 * @param {number} productId
 * @param {number} quantity
 * @param {number} exchangeRate
 */
export async function addItemToSale(saleId, productId, quantity, exchangeRate) {
  return await api.post(`/api/sales/${saleId}/items`, {
    productId,
    quantity,
    exchangeRate,
  });
}

/**
 * Actualiza la cantidad de un ítem en la venta.
 * @param {number} saleId
 * @param {number} itemId
 * @param {number} quantity
 * @param {number} exchangeRate
 */
export async function updateItemQuantity(saleId, itemId, quantity, exchangeRate) {
  return await api.put(`/api/sales/${saleId}/items/${itemId}`, {
    quantity,
    exchangeRate,
  });
}

/**
 * Elimina un ítem de la venta.
 * @param {number} saleId
 * @param {number} itemId
 * @param {number} exchangeRate
 */
export async function removeItemFromSale(saleId, itemId, exchangeRate) {
  return await api.delete(`/api/sales/${saleId}/items/${itemId}?exchangeRate=${exchangeRate}`);
}

/**
 * Actualiza la lista de precios de la venta ("Retail" o "Wholesale").
 * @param {number} saleId
 * @param {string} priceListType
 */
export async function updatePriceList(saleId, priceListType) {
  return await api.put(`/api/sales/${saleId}/price-list`, { priceListType });
}

/**
 * Recalcula la venta con una nueva tasa de cambio.
 * @param {number} saleId
 * @param {number} exchangeRate
 */
export async function updateSaleExchangeRate(saleId, exchangeRate) {
  return await api.put(`/api/sales/${saleId}/exchange-rate?exchangeRate=${exchangeRate}`);
}

/**
 * Pone una venta en espera asignando un cliente y opcionalmente un abono inicial.
 */
export async function holdSale(saleId, requestData, exchangeRate = 0, initialPayment = null) {
  if (typeof requestData === 'object' && requestData !== null) {
    return await api.post(`/api/sales/${saleId}/hold`, requestData);
  }
  return await api.post(`/api/sales/${saleId}/hold`, {
    customerId: requestData,
    exchangeRate,
    initialPayment,
  });
}

/**
 * Registra un abono parcial en una venta en espera.
 */
export async function addPaymentToHoldSale(saleId, paymentReq) {
  return await api.post(`/api/sales/${saleId}/payments`, paymentReq);
}

/**
 * Obtiene las ventas que se encuentran en espera (OnHold / Cuentas abiertas).
 */
export async function getPendingSales() {
  return await api.get('/api/sales/pending');
}

/**
 * Completa la venta procesando los pagos finales.
 * @param {number} saleId
 * @param {number} exchangeRate
 * @param {Array} payments - [{ paymentMethodId, amount, amountLocal, referenceNumber }]
 * @param {number} roundingAdjustment
 * @param {number} [cashierId]
 * @returns {Promise<number>} Número de factura
 */
export async function completeSale(saleId, exchangeRate, payments, roundingAdjustment = 0, cashierId = null, isPendingPickup = false) {
  return await api.post(`/api/sales/${saleId}/complete`, {
    exchangeRate,
    roundingAdjustment,
    cashierId,
    isPendingPickup,
    payments,
  });
}

/**
 * Actualiza el cliente asociado a una venta.
 * @param {number} saleId
 * @param {number} customerId
 */
export async function updateSaleCustomer(saleId, customerId) {
  return await api.put(`/api/sales/${saleId}/customer`, {
    customerId
  });
}

/**
 * Actualiza la lista de productos de una venta en espera (OnHold).
 * @param {number} saleId
 * @param {Array} items - [{ productId, quantity, unitPrice }]
 */
export async function updateSaleItems(saleId, items) {
  return await api.put(`/api/sales/${saleId}/items`, {
    items
  });
}
