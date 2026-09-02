/**
 * Módulo de detección de códigos de barras acelerado por hardware nativo (Shape Detection API).
 * Implementa comprobaciones estrictas de formatos 1D para compatibilidad con Safari iOS 17+, Chrome y Edge.
 */

export const SUPPORTED_1D_FORMATS = [
  'ean_13',
  'ean_8',
  'upc_a',
  'upc_e',
  'code_128',
  'code_39',
  'itf',
  'codabar',
];

/**
 * Valida si el navegador soporta la API nativa BarcodeDetector y si soporta simbologías 1D.
 * En Safari iOS 17+ u otros navegadores con soporte parcial (solo QR), retorna false para activar ZXing.
 * @returns {Promise<boolean>}
 */
export async function checkBarcodeDetectorSupport() {
  if (typeof window === 'undefined' || !('BarcodeDetector' in window)) {
    return false;
  }

  try {
    if (typeof window.BarcodeDetector.getSupportedFormats !== 'function') {
      return false;
    }

    const formats = await window.BarcodeDetector.getSupportedFormats();
    if (!Array.isArray(formats) || formats.length === 0) {
      return false;
    }

    // Guarda estricta: debe soportar al menos EAN-13 y Code-128 para entorno retail/POS
    const hasEan13 = formats.includes('ean_13');
    const hasCode128 = formats.includes('code_128');

    return Boolean(hasEan13 && hasCode128);
  } catch {
    return false;
  }
}

/**
 * Crea una instancia de BarcodeDetector configurada para formatos 1D retail.
 * @returns {BarcodeDetector|null}
 */
export function createNativeBarcodeDetector() {
  if (typeof window === 'undefined' || !('BarcodeDetector' in window)) {
    return null;
  }

  try {
    return new window.BarcodeDetector({
      formats: SUPPORTED_1D_FORMATS,
    });
  } catch (err) {
    console.warn('[NativeBarcodeScanner] Error al instanciar BarcodeDetector nativo:', err);
    return null;
  }
}
