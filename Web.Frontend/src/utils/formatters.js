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



let cachedCurrencyFormat = 'Venezuelan';
if (typeof window !== 'undefined' && window.localStorage) {
  const saved = window.localStorage.getItem('currency_format_preference');
  if (saved === 'Venezuelan' || saved === 'International') {
    cachedCurrencyFormat = saved;
  }
}

/**
 * Retorna la preferencia actual de formato de moneda ('Venezuelan' | 'International').
 * @returns {'Venezuelan' | 'International'}
 */
export function getCurrencyFormat() {
  return cachedCurrencyFormat;
}

/**
 * Actualiza la preferencia en caché en memoria y en localStorage.
 * @param {'Venezuelan' | 'International'} format
 */
export function setCachedCurrencyFormat(format) {
  if (format === 'Venezuelan' || format === 'International') {
    cachedCurrencyFormat = format;
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem('currency_format_preference', format);
    }
  }
}

/**
 * Formats a numeric value according to the specified or active format:
 * - Venezuelan: 1250.5 -> "1.250,50", 172786.94 -> "172.786,94"
 * - International: 1250.5 -> "1,250.50", 172786.94 -> "172,786.94"
 *
 * @param {number|string} value
 * @param {'Venezuelan'|'International'} [format]
 * @param {number} [decimals]
 * @returns {string}
 */
export function formatAmount(value, format = getCurrencyFormat(), decimals = 2) {
  const effectiveFormat = (format === 'International') ? 'International' : 'Venezuelan';
  const dec = typeof decimals === 'number' && decimals >= 0 ? decimals : 2;

  if (value === null || value === undefined || value === '') {
    return effectiveFormat === 'Venezuelan'
      ? (0).toFixed(dec).replace('.', ',')
      : (0).toFixed(dec);
  }

  const num = typeof value === 'number' ? value : parseFloat(value);
  if (isNaN(num)) {
    return effectiveFormat === 'Venezuelan'
      ? (0).toFixed(dec).replace('.', ',')
      : (0).toFixed(dec);
  }

  const isNegative = num < 0;
  const absNum = Math.abs(num);
  const parts = absNum.toFixed(dec).split('.');
  const integerPartRaw = parts[0];
  const decimalPartRaw = parts[1];

  let integerPartFormatted = '';
  if (effectiveFormat === 'Venezuelan') {
    // Miles con punto .
    integerPartFormatted = integerPartRaw.replace(/\B(?=(\d{3})+(?!\d))/g, '.');
    const result = dec > 0 ? `${integerPartFormatted},${decimalPartRaw}` : integerPartFormatted;
    return isNegative ? `-${result}` : result;
  } else {
    // Miles con coma ,
    integerPartFormatted = integerPartRaw.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    const result = dec > 0 ? `${integerPartFormatted}.${decimalPartRaw}` : integerPartFormatted;
    return isNegative ? `-${result}` : result;
  }
}

/**
 * Compatibilidad con código existente: Formatea un valor numérico usando el formato activo
 * @param {number|string} amount
 * @param {number} decimals
 * @param {'Venezuelan'|'International'} [format]
 * @returns {string}
 */
export function formatNumberEs(amount, decimals = 2, format = getCurrencyFormat()) {
  return formatAmount(amount, format, decimals);
}

/**
 * Formats Bolívares: 1250.5 -> "Bs.S 1.250,50" (Venezolano) o "Bs.S 1,250.50" (Internacional)
 * @param {number|string} amount
 * @param {number} decimals
 * @param {'Venezuelan'|'International'} [format]
 * @returns {string}
 */
export function formatBsS(amount, decimals = 2, format = getCurrencyFormat()) {
  return `Bs.S ${formatAmount(amount, format, decimals)}`;
}

/**
 * Formats USD: 1250.5 -> "$ 1.250,50" (Venezolano) o "$ 1,250.50" (Internacional)
 * @param {number|string} amount
 * @param {number} decimals
 * @param {'Venezuelan'|'International'} [format]
 * @returns {string}
 */
export function formatUSD(amount, decimals = 2, format = getCurrencyFormat()) {
  return `$ ${formatAmount(amount, format, decimals)}`;
}

/**
 * Parsea cadenas numéricas de forma flexible y segura:
 * Regla:
 * - Vacío, nulo, inválido -> 0
 * - Sin separadores -> entero ("72915" -> 72915.00)
 * - Un solo separador (punto o coma) -> se asume decimal ("172,94" -> 172.94, "172.94" -> 172.94)
 * - Dos o más separadores -> el ÚLTIMO separador es decimal, los precedentes son de miles:
 *   "172.786,94" -> 172786.94
 *   "172,786.94" -> 172786.94
 *   "1.234.567,89" -> 1234567.89
 * - Caracteres inválidos o separadores dobles continuos (ej. ",,", "..") -> 0
 *
 * @param {number|string} rawValue
 * @returns {number}
 */
export function parseAmount(rawValue) {
  if (rawValue === null || rawValue === undefined) return 0;
  if (typeof rawValue === 'number') return isNaN(rawValue) ? 0 : rawValue;

  let str = String(rawValue).trim();
  if (!str) return 0;

  // Manejar signo negativo
  const isNegative = str.startsWith('-');
  if (isNegative) {
    str = str.substring(1).trim();
  }

  // Eliminar prefijos de moneda como "Bs.S", "Bs", "$"
  str = str.replace(/^(Bs\.S|Bs|\$)\s*/i, '').trim();
  if (!str) return 0;

  // Separadores consecutivos inválidos (ej. ",,", "..", ".,", ",.")
  if (/[,.]{2,}/.test(str)) {
    return 0;
  }

  // Si contiene caracteres extraños (no dígitos ni . ni ,)
  if (/[^\d.,]/.test(str)) {
    return 0;
  }

  const hasDot = str.includes('.');
  const hasComma = str.includes(',');

  // Caso 1: Sin separadores
  if (!hasDot && !hasComma) {
    const num = parseFloat(str);
    return isNaN(num) ? 0 : (isNegative ? -num : num);
  }

  const matches = str.match(/[,.]/g) || [];

  // Caso 2: Exactamente un separador -> se asume decimal
  if (matches.length === 1) {
    const normalized = str.replace(/[,.]/, '.');
    const num = parseFloat(normalized);
    return isNaN(num) ? 0 : (isNegative ? -num : num);
  }

  // Caso 3: Dos o más separadores -> el ÚLTIMO es decimal, los demás son miles
  const lastDot = str.lastIndexOf('.');
  const lastComma = str.lastIndexOf(',');
  const lastSepIndex = Math.max(lastDot, lastComma);

  const intPart = str.substring(0, lastSepIndex).replace(/[.,]/g, '');
  const decPart = str.substring(lastSepIndex + 1);

  const normalized = `${intPart}.${decPart}`;
  const num = parseFloat(normalized);
  return isNaN(num) ? 0 : (isNegative ? -num : num);
}

/**
 * Alias de compatibilidad hacia atrás
 */
export function parseFormattedNumber(val) {
  return parseAmount(val);
}

/**
 * 8.9-M18: normalización canónica de montos a escala entera de centésimas.
 * Convierte cualquier entrada que parseAmount entienda (incluido >2 decimales) al valor
 * monetario redondeado al céntimo más próximo (regla única usada por AtmAmountInput y
 * PaymentForm para que ambos redondeen idéntico).
 * @param {number|string} rawValue
 * @returns {number} centésimas (ej: "172.786" -> 17279)
 */
export function amountToCents(rawValue) {
  const num = parseAmount(rawValue);
  if (!isFinite(num)) return 0;
  return Math.round(num * 100);
}

/**
 * Convierte centésimas al número decimal equivalente (ej: 17279 -> 172.79).
 * @param {number} cents
 * @returns {number}
 */
export function centsToAmount(cents) {
  const n = Number(cents);
  return isFinite(n) ? n / 100 : 0;
}

/**
 * ATM-style input formatting: shifts typed digits to cents
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
  return isNaN(d.getTime()) ? '-' : d.toLocaleDateString('es-VE', { day: '2-digit', month: '2-digit', year: 'numeric' });
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
