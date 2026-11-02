// 8.211-T4b1 (S9): contrato puro del perfil monetario de presentación. No contiene aritmética
// financiera: la normalización solo resuelve modo/códigos/símbolos y delega el formato numérico
// en formatAmount (misma fuente que el resto de la Web).
import { formatAmount } from './formatters';

export const MONETARY_MODE_SINGLE = 'Single';
export const MONETARY_MODE_DUAL = 'Dual';

export const DEFAULT_MONETARY_PROFILE = Object.freeze({
  mode: MONETARY_MODE_DUAL,
  primaryCurrencyCode: 'USD',
  primaryCurrencySymbol: '$',
  secondaryCurrencyCode: 'VES',
  secondaryCurrencySymbol: 'Bs.S',
  isConfigured: false,
});

const DEFAULT_SYMBOL_BY_CODE = Object.freeze({
  USD: '$',
  VES: 'Bs.S',
});

const VES_ALIASES = new Set(['VES', 'BS.S', 'BSS']);
const ISO_ALPHA3 = /^[A-Z]{3}$/;
const MAX_SYMBOL_LENGTH = 8;

/**
 * Canonicaliza un código de moneda a ISO 4217 alfa-3. Los alias legacy de VES se resuelven al
 * código ISO (`VES`); cualquier valor ausente o malformado devuelve `null`.
 * @param {unknown} value
 * @returns {string | null}
 */
export function normalizeCurrencyCode(value) {
  if (typeof value !== 'string') return null;

  const normalized = value.trim().toUpperCase();
  if (!normalized) return null;
  if (VES_ALIASES.has(normalized)) return 'VES';
  return ISO_ALPHA3.test(normalized) ? normalized : null;
}

/**
 * Código de presentación: `VES` se muestra como `Bs.S` por compatibilidad con la UI histórica;
 * el resto conserva su código ISO.
 * @param {unknown} value
 * @returns {string}
 */
export function displayCurrencyCode(value) {
  const canonical = normalizeCurrencyCode(value);
  if (!canonical) return '';
  return canonical === 'VES' ? 'Bs.S' : canonical;
}

function defaultSymbolForCode(value) {
  const canonical = normalizeCurrencyCode(value);
  if (!canonical) return '';
  return DEFAULT_SYMBOL_BY_CODE[canonical] ?? canonical;
}

function normalizeMode(value) {
  if (typeof value === 'number') {
    if (value === 0) return MONETARY_MODE_SINGLE;
    if (value === 1) return MONETARY_MODE_DUAL;
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase();
    if (normalized === 'single' || normalized === '0') return MONETARY_MODE_SINGLE;
    if (normalized === 'dual' || normalized === '1') return MONETARY_MODE_DUAL;
  }

  return MONETARY_MODE_DUAL;
}

function containsControlCharacter(value) {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code <= 0x1F || (code >= 0x7F && code <= 0x9F) || code === 0x2028 || code === 0x2029) {
      return true;
    }
  }
  return false;
}

function normalizeSymbol(value, fallback) {
  if (typeof value !== 'string') return fallback;

  const trimmed = value.trim();
  if (!trimmed || trimmed.length > MAX_SYMBOL_LENGTH || containsControlCharacter(trimmed)) {
    return fallback;
  }

  return trimmed;
}

/**
 * Normaliza el DTO del backend sin lanzar nunca. Los campos ausentes o malformados caen a los
 * defaults compatibles con el legado; `Single` siempre limpia el slot secundario. En `Dual`, un
 * par con la misma moneda (payload inválido) cae a una secundaria distinta y segura.
 * @param {unknown} raw
 * @returns {{
 *   mode: 'Single' | 'Dual',
 *   primaryCurrencyCode: string,
 *   primaryCurrencySymbol: string,
 *   secondaryCurrencyCode: string | null,
 *   secondaryCurrencySymbol: string | null,
 *   isConfigured: boolean
 * }}
 */
export function normalizeMonetaryProfile(raw) {
  try {
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      return { ...DEFAULT_MONETARY_PROFILE };
    }

    const payload = /** @type {Record<string, unknown>} */ (raw);
    const mode = normalizeMode(payload.mode);
    const primaryCurrencyCode = normalizeCurrencyCode(payload.primaryCurrencyCode)
      ?? DEFAULT_MONETARY_PROFILE.primaryCurrencyCode;
    const primaryCurrencySymbol = normalizeSymbol(
      payload.primaryCurrencySymbol,
      defaultSymbolForCode(primaryCurrencyCode)
    );

    let secondaryCurrencyCode = null;
    let secondaryCurrencySymbol = null;

    if (mode === MONETARY_MODE_DUAL) {
      const requestedSecondaryCode = normalizeCurrencyCode(payload.secondaryCurrencyCode);
      const fallbackSecondaryCode = primaryCurrencyCode === 'USD' ? 'VES' : 'USD';
      secondaryCurrencyCode = requestedSecondaryCode && requestedSecondaryCode !== primaryCurrencyCode
        ? requestedSecondaryCode
        : fallbackSecondaryCode;
      secondaryCurrencySymbol = normalizeSymbol(
        payload.secondaryCurrencySymbol,
        defaultSymbolForCode(secondaryCurrencyCode)
      );
    }

    return {
      mode,
      primaryCurrencyCode,
      primaryCurrencySymbol,
      secondaryCurrencyCode,
      secondaryCurrencySymbol,
      isConfigured: payload.isConfigured === true,
    };
  } catch {
    return { ...DEFAULT_MONETARY_PROFILE };
  }
}

/**
 * Resuelve el símbolo configurado para un código. Acepta alias legacy (`Bs.S`, `BsS`) y cae a
 * los defaults USD/$ y VES/Bs.S cuando el perfil no contiene el código; un código ISO ajeno al
 * perfil se presenta con su propio código para no inventar un símbolo.
 * @param {unknown} profile
 * @param {unknown} code
 * @returns {string}
 */
export function symbolForCode(profile, code) {
  const normalizedProfile = normalizeMonetaryProfile(profile);
  const canonical = normalizeCurrencyCode(code);
  if (!canonical) return '';

  if (canonical === normalizedProfile.primaryCurrencyCode) {
    return normalizedProfile.primaryCurrencySymbol;
  }

  if (normalizedProfile.mode === MONETARY_MODE_DUAL && canonical === normalizedProfile.secondaryCurrencyCode) {
    return normalizedProfile.secondaryCurrencySymbol;
  }

  return defaultSymbolForCode(canonical);
}

/**
 * Compone símbolo + monto usando `formatAmount` (no duplica redondeo ni separadores). Un símbolo
 * vacío o inválido deja únicamente el monto formateado.
 * @param {unknown} symbol
 * @param {unknown} amount
 * @param {'Venezuelan' | 'International' | undefined} format
 * @param {number} [decimals]
 * @returns {string}
 */
export function formatWithSymbol(symbol, amount, format, decimals = 2) {
  const formattedAmount = formatAmount(amount, format, decimals);
  const safeSymbol = normalizeSymbol(symbol, '');
  return safeSymbol ? `${safeSymbol} ${formattedAmount}` : formattedAmount;
}
