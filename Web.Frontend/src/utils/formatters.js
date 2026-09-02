/**
 * Formats product quantity (integer or fractional up to 3 decimals without trailing zeroes)
 * 1.000 -> "1"
 * 1.500 -> "1.5"
 * 1.750 -> "1.75"
 */
export function formatQuantity(qty) {
  if (qty === null || qty === undefined || isNaN(qty)) return '0';
  const num = typeof qty === 'number' ? qty : parseFloat(qty);
  if (isNaN(num)) return '0';
  return num % 1 === 0 ? num.toString() : num.toFixed(3).replace(/\.?0+$/, '');
}



/**
 * Formats a numeric value: 1250.5 -> "1,250.50"
 * @param {number|string} amount
 * @param {number} decimals
 * @returns {string}
 */
export function formatNumberEs(amount, decimals = 2) {
  if (amount === null || amount === undefined || isNaN(amount)) {
    return (0).toFixed(decimals);
  }
  const num = typeof amount === 'number' ? amount : parseFloat(amount);
  if (isNaN(num)) return (0).toFixed(decimals);

  const parts = num.toFixed(decimals).split('.');
  const integerPart = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const decimalPart = parts[1];

  return decimals > 0 ? `${integerPart}.${decimalPart}` : integerPart;
}

/**
 * Formats Bolívares: 1250.5 -> "Bs.S 1,250.50"
 */
export function formatBsS(amount, decimals = 2) {
  return `Bs.S ${formatNumberEs(amount, decimals)}`;
}

/**
 * Formats USD: 1250.5 -> "$ 1,250.50"
 */
export function formatUSD(amount, decimals = 2) {
  return `$ ${formatNumberEs(amount, decimals)}`;
}

/**
 * Parses a formatted string ("1,250.50") back to number (1250.50)
 */
export function parseFormattedNumber(val) {
  if (val === null || val === undefined) return 0;
  if (typeof val === 'number') return val;
  const str = String(val).trim();
  if (!str) return 0;
  // Remove thousand commas and parse decimal dot
  const cleanStr = str.replace(/,/g, '');
  const parsed = parseFloat(cleanStr);
  return isNaN(parsed) ? 0 : parsed;
}

/**
 * ATM-style input formatting: shifts typed digits to cents
 * "125050" -> "1,250.50"
 * "50" -> "0.50"
 * "5" -> "0.05"
 *
 * With decimals=0 (modo entero): "125050" -> "125,050" (monto entero, sin centavos)
 */
export function formatAtmInput(rawValue, decimals = 2) {
  const digits = String(rawValue || '').replace(/\D/g, '');
  if (!digits) return '';
  const num = parseInt(digits, 10) / (decimals > 0 ? 100 : 1);
  return formatNumberEs(num, decimals);
}

/**
 * Formats a date value (ISO string / Date) as es-VE locale date: "2026-08-13" -> "13/8/2026"
 */
export function formatDate(value) {
  if (!value) return '-';
  const d = new Date(value);
  return isNaN(d.getTime()) ? '-' : d.toLocaleDateString('es-VE');
}

/**
 * Formats a date value (ISO string / Date) as es-VE locale time (HH:mm): "2026-08-13T23:16:41" -> "11:16 p. m."
 */
export function formatTime(value) {
  if (!value) return '';
  const d = new Date(value);
  return isNaN(d.getTime()) ? '' : d.toLocaleTimeString('es-VE', { hour: '2-digit', minute: '2-digit' });
}

/**
 * Calculates exact line amounts in Bs.S and USD for cart/order items,
 * prioritizing persistent local currency values (unitPriceBsS, subtotalBsS)
 * and falling back gracefully when not available.
 * 
 * @param {object} item
 * @param {number} fallbackExchangeRate
 * @returns {{ unitBsS: number, subtotalBsS: number, unitUSD: number, subtotalUSD: number }}
 */
export function getLineAmounts(item, fallbackExchangeRate = 1) {
  if (!item) return { unitBsS: 0, subtotalBsS: 0, unitUSD: 0, subtotalUSD: 0 };
  
  const qty = Number(item.quantity) || 0;
  const rate = Number(item.appliedRate || fallbackExchangeRate || 1);
  
  const unitUSD = Number(item.unitPrice) || 0;
  const subtotalUSD = item.subtotal !== undefined ? Number(item.subtotal) : (qty * unitUSD);
  
  const unitBsS = Number(item.unitPriceBsS) > 0 
    ? Number(item.unitPriceBsS) 
    : (unitUSD > 0 ? unitUSD * rate : 0);
    
  const subtotalBsS = Number(item.subtotalBsS) > 0 
    ? Number(item.subtotalBsS) 
    : (unitBsS > 0 ? qty * unitBsS : subtotalUSD * rate);
    
  return { unitBsS, subtotalBsS, unitUSD, subtotalUSD };
}

/**
 * Formats a product's price for catalog views, returning '—' if it's a group header with independent pricing.
 * @param {object} product
 * @param {boolean} isWholesale
 * @param {string} currency 'Bs.S' | 'USD'
 * @param {number} exchangeRate
 * @returns {string}
 */
export function formatProductDisplayPrice(product, isWholesale = false, currency = 'Bs.S', exchangeRate = 1) {
  if (!product) return '—';
  if (product.isGroupHeader && product.hasIndependentPricing) {
    return '—';
  }

  const retailUSD = product.priceUSD || 0;
  const retailBsS = product.priceUSD > 0 ? product.priceUSD * exchangeRate : (product.priceBsS || 0);

  if (!isWholesale) {
    return currency === 'USD' ? formatUSD(retailUSD) : formatBsS(retailBsS);
  }

  const hasRealWholesale = (product.hasWholesale || product.priceWholesaleUSD > 0) && product.priceWholesaleUSD > 0 && product.priceWholesaleUSD < retailUSD;
  const wholesaleUSD = hasRealWholesale ? product.priceWholesaleUSD : retailUSD;
  const wholesaleBsS = hasRealWholesale ? product.priceWholesaleUSD * exchangeRate : retailBsS;

  return currency === 'USD' ? formatUSD(wholesaleUSD) : formatBsS(wholesaleBsS);
}
