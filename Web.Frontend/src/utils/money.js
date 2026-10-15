/**
 * Rounds a USD monetary amount to 2 decimal places.
 * @param {number|string} amount
 * @returns {number}
 */
export function roundUsd(amount) {
  const num = Number(amount);
  if (!Number.isFinite(num)) return 0;
  return Math.round(num * 100) / 100;
}

/**
 * Rounds a Bs.S monetary amount to 2 decimal places.
 * @param {number|string} amount
 * @returns {number}
 */
export function roundBs(amount) {
  const num = Number(amount);
  if (!Number.isFinite(num)) return 0;
  return Math.round(num * 100) / 100;
}

/**
 * Converts a USD amount to Bs.S using ceiling rounding to 2 decimal places.
 * @param {number|string} usd
 * @param {number|string} rate
 * @returns {number}
 */
export function usdToBs(usd, rate) {
  const u = Number(usd) || 0;
  const r = Number(rate) || 0;
  return u > 0 && r > 0 ? Math.ceil(u * r * 100) / 100 : 0;
}

/**
 * Converts a Bs.S amount to USD preserving 4 decimal places of the official BCV exchange rate in integer scale.
 * @param {number|string} bs
 * @param {number|string} rate
 * @returns {number}
 */
export function bsToUsd(bs, rate) {
  const amountBs = Number(bs) || 0;
  const exchangeRate = Number(rate) || 0;
  if (exchangeRate <= 0 || amountBs <= 0) return 0;
  const parsedCents = Math.round(amountBs * 100);
  const rateScaled = Math.round(exchangeRate * 10000);
  if (rateScaled <= 0) return 0;
  const usdCents = Math.round((parsedCents * 10000) / rateScaled);
  return usdCents / 100;
}
