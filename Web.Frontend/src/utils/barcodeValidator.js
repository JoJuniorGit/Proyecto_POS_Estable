/**
 * Validador para lecturas de códigos de barras (1D).
 * Restringe la captura exclusivamente a códigos de barras estándar de productos,
 * aplicando validación matemática de dígito verificador (GS1 Módulo 10) para formatos comerciales
 * estándar (EAN-13, UPC-A, EAN-8 y códigos de balanza 20-29), y admitiendo códigos alfanuméricos
 * internos (Code-128, Code-39, SKUs cortos de 4 a 15 caracteres) sin caracteres especiales ni URLs.
 * 
 * NOTA SOBRE CÓDIGOS DE BALANZA (Prefijos GS1 20 a 29):
 * En entornos minoristas, los códigos de balanza para productos de peso variable siguen la
 * estructura '20AAAAA CCCCC K' (13 dígitos con checksum Mod10 estándar calculado por la balanza).
 * En esta fase, el validador comprueba la integridad del formato y checksum para no rechazarlos
 * erróneamente. La descomposición del payload de peso/importe se gestiona en la capa de venta.
 */

const STANDARD_BARCODE_REGEX = /^[A-Za-z0-9]{4,15}$/;

/**
 * Valida el dígito verificador matemático según el algoritmo estándar GS1 Módulo 10.
 * Ponderación alternada 3, 1 desde el penúltimo dígito hacia la izquierda.
 * @param {string} digits Cadena numérica
 * @returns {boolean}
 */
export function validateGs1Mod10Checksum(digits) {
  if (!digits || (digits.length !== 8 && digits.length !== 12 && digits.length !== 13)) {
    return false;
  }

  const checkDigit = Number.parseInt(digits[digits.length - 1], 10);
  if (Number.isNaN(checkDigit)) return false;

  let sum = 0;
  let weight = 3;

  for (let i = digits.length - 2; i >= 0; i--) {
    const d = Number.parseInt(digits[i], 10);
    if (Number.isNaN(d)) return false;
    sum += d * weight;
    weight = weight === 3 ? 1 : 3;
  }

  const calculatedCheck = (10 - (sum % 10)) % 10;
  return calculatedCheck === checkDigit;
}

/**
 * Valida si una cadena capturada corresponde a un código de barras de producto válido.
 * @param {string} code
 * @returns {boolean}
 */
export function isValidBarcode(code) {
  if (!code || typeof code !== 'string') return false;

  const trimmed = code.trim();

  // Filtro de longitud (4 a 15 caracteres)
  if (trimmed.length < 4 || trimmed.length > 15) {
    return false;
  }

  // Filtro de caracteres típicos de QR / URLs
  if (
    trimmed.includes('http://') ||
    trimmed.includes('https://') ||
    trimmed.includes('://') ||
    trimmed.includes('?') ||
    trimmed.includes('=') ||
    trimmed.includes('&') ||
    trimmed.includes('/') ||
    trimmed.includes('\\') ||
    trimmed.includes(':') ||
    trimmed.includes('#') ||
    /\s/.test(trimmed)
  ) {
    return false;
  }

  if (!STANDARD_BARCODE_REGEX.test(trimmed)) {
    return false;
  }

  // Validación format-aware de checksum para EAN-13 (balanzas 20-29 inc.), UPC-A y EAN-8 numéricos
  if ((trimmed.length === 8 || trimmed.length === 12 || trimmed.length === 13) && /^\d+$/.test(trimmed)) {
    return validateGs1Mod10Checksum(trimmed);
  }

  return true;
}
