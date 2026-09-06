/**
 * laptopVisionEnhancer.js
 * Pipeline de visión artificial de alta efectividad exclusivo para webcams de laptops / PCs.
 * 
 * Medidas drásticas implementadas:
 * 1. Recorte Central Multi-Escala (Zoom Digital ROI 2.4x / 1.8x).
 * 2. Binarización Adaptativa Local de Sauvola con Imagen Integral O(1).
 * 3. Realce Direccional Horizontal 1D (Específico para códigos de barras verticales).
 * 4. Inversión de luminancia para códigos en fondos oscuros o empaques metalizados.
 */

/**
 * Detecta si el entorno actual es una Laptop o PC de escritorio.
 * @returns {boolean}
 */
export function isLaptopOrDesktopEnvironment() {
  if (typeof window === 'undefined' || typeof navigator === 'undefined') return false;

  const isMobileUa = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(
    navigator.userAgent || ''
  );
  if (isMobileUa) return false;

  if (navigator.userAgentData && typeof navigator.userAgentData.mobile === 'boolean') {
    if (navigator.userAgentData.mobile) return false;
  }

  if (window.matchMedia) {
    const hasFinePointer = window.matchMedia('(pointer: fine)').matches;
    const hasCoarsePointerOnly = window.matchMedia('(pointer: coarse) and not (pointer: fine)').matches;
    if (hasFinePointer && !hasCoarsePointerOnly) return true;
  }

  return true;
}

/**
 * Realce de bordes direccional 1D horizontal (Unsharp Mask horizontal).
 * Enfoca exclusivamente las transiciones de las barras verticales sin amplificar ruido vertical.
 */
export function apply1DHorizontalSharpen(imageData, strength = 1.0) {
  const { data, width, height } = imageData;
  const copy = new Uint8ClampedArray(data);

  for (let y = 0; y < height; y++) {
    const rowOffset = y * width * 4;
    for (let x = 1; x < width - 1; x++) {
      const idx = rowOffset + x * 4;
      const leftIdx = rowOffset + (x - 1) * 4;
      const rightIdx = rowOffset + (x + 1) * 4;

      for (let c = 0; c < 3; c++) {
        const center = copy[idx + c];
        const left = copy[leftIdx + c];
        const right = copy[rightIdx + c];

        // Derivada de segundo orden horizontal
        const sharpened = center + strength * (2 * center - left - right);
        data[idx + c] = sharpened < 0 ? 0 : sharpened > 255 ? 255 : (sharpened | 0);
      }
    }
  }

  return imageData;
}

/**
 * Binarización adaptativa local de Sauvola con Imagen Integral O(1).
 * Elimina sombras desparejas, gradientes de luz de oficina y reflejos de bolsas plásticas.
 */
export function applySauvolaBinarization(imageData, windowRadius = 8, k = 0.18, R = 128) {
  const { data, width, height } = imageData;
  const numPixels = width * height;

  // 1. Extraer luminancia en escala de grises
  const gray = new Uint8Array(numPixels);
  for (let i = 0; i < numPixels; i++) {
    const idx = i * 4;
    gray[i] = ((data[idx] * 299 + data[idx + 1] * 587 + data[idx + 2] * 114 + 500) / 1000) | 0;
  }

  // 2. Construir imágenes integrales (suma y suma de cuadrados)
  const intSum = new Float64Array((width + 1) * (height + 1));
  const intSqSum = new Float64Array((width + 1) * (height + 1));

  for (let y = 0; y < height; y++) {
    let rowSum = 0;
    let rowSqSum = 0;
    const srcRow = y * width;
    const destRow = (y + 1) * (width + 1);
    const prevRow = y * (width + 1);

    for (let x = 0; x < width; x++) {
      const val = gray[srcRow + x];
      rowSum += val;
      rowSqSum += val * val;

      const destIdx = destRow + (x + 1);
      const prevIdx = prevRow + (x + 1);

      intSum[destIdx] = intSum[prevIdx] + rowSum;
      intSqSum[destIdx] = intSqSum[prevIdx] + rowSqSum;
    }
  }

  // 3. Aplicar umbral de Sauvola local en cada píxel
  for (let y = 0; y < height; y++) {
    const y0 = Math.max(0, y - windowRadius);
    const y1 = Math.min(height - 1, y + windowRadius);
    const rowOffset = y * width;

    for (let x = 0; x < width; x++) {
      const x0 = Math.max(0, x - windowRadius);
      const x1 = Math.min(width - 1, x + windowRadius);

      const area = (x1 - x0 + 1) * (y1 - y0 + 1);

      // Coordenadas en la imagen integral
      const A = y0 * (width + 1) + x0;
      const B = y0 * (width + 1) + (x1 + 1);
      const C = (y1 + 1) * (width + 1) + x0;
      const D = (y1 + 1) * (width + 1) + (x1 + 1);

      const sum = intSum[D] - intSum[B] - intSum[C] + intSum[A];
      const sqSum = intSqSum[D] - intSqSum[B] - intSqSum[C] + intSqSum[A];

      const mean = sum / area;
      const variance = Math.max(0, (sqSum - (sum * sum) / area) / area);
      const stdDev = Math.sqrt(variance);

      // Fórmula de Sauvola
      const threshold = mean * (1.0 + k * (stdDev / R - 1.0));

      const pixelIdx = (rowOffset + x) * 4;
      const binaryVal = gray[rowOffset + x] < threshold ? 0 : 255;

      data[pixelIdx] = binaryVal;
      data[pixelIdx + 1] = binaryVal;
      data[pixelIdx + 2] = binaryVal;
    }
  }

  return imageData;
}

/**
 * Invierte la luminancia del buffer para leer códigos negativos (barras claras en fondo oscuro).
 */
export function applyLuminanceInversion(imageData) {
  const { data } = imageData;
  for (let i = 0; i < data.length; i += 4) {
    data[i] = 255 - data[i];
    data[i + 1] = 255 - data[i + 1];
    data[i + 2] = 255 - data[i + 2];
  }
  return imageData;
}

/**
 * Prepara y procesa un fotograma según el paso del pipeline multi-fase.
 * @param {HTMLVideoElement} video
 * @param {HTMLCanvasElement} canvas
 * @param {number} passIndex (0: Sauvola Binarized, 1: 1D Horizontal Sharpen, 2: Raw High-Res Crop, 3: Invertido)
 * @returns {ImageData|null}
 */
export function processMultiPassLaptopFrame(video, canvas, passIndex = 0) {
  if (!video || !canvas || video.readyState < 2) return null;

  const vw = video.videoWidth || 1280;
  const vh = video.videoHeight || 720;

  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) return null;

  // Zoom central 2.2x para máxima densidad de píxeles a distancia focal nítida (35-45 cm)
  const zoomFactor = passIndex === 2 ? 0.55 : 0.45;
  const sw = vw * zoomFactor;
  const sh = vh * zoomFactor;
  const sx = (vw - sw) / 2;
  const sy = (vh - sh) / 2;

  const dw = (canvas.width = Math.round(sw));
  const dh = (canvas.height = Math.round(sh));

  ctx.drawImage(video, sx, sy, sw, sh, 0, 0, dw, dh);

  if (passIndex === 2) {
    // Paso 2: Fotograma original recortado en alta resolución
    return ctx.getImageData(0, 0, dw, dh);
  }

  const imageData = ctx.getImageData(0, 0, dw, dh);

  if (passIndex === 0) {
    // Paso 0: Sauvola Binarización Local Adaptativa
    applySauvolaBinarization(imageData, 8, 0.18);
  } else if (passIndex === 1) {
    // Paso 1: Realce Direccional Horizontal 1D
    apply1DHorizontalSharpen(imageData, 1.2);
  } else if (passIndex === 3) {
    // Paso 3: Inversión de contraste
    applySauvolaBinarization(imageData, 8, 0.18);
    applyLuminanceInversion(imageData);
  }

  ctx.putImageData(imageData, 0, 0);
  return imageData;
}
