/**
 * Utility helper functions for currency & number formatting
 * Thousands separator: ',' (comma)
 * Decimal separator: '.' (dot)
 */

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
 */
export function formatAtmInput(rawValue) {
  const digits = String(rawValue || '').replace(/\D/g, '');
  if (!digits) return '';
  const num = parseInt(digits, 10) / 100;
  return formatNumberEs(num, 2);
}
