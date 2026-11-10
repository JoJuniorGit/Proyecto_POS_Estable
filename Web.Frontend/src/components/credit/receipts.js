/**
 * 8.187-T5 (Paso 11/12, F4, S5): guardado de comprobantes PDF del crédito CxC.
 * Los endpoints de comprobantes devuelven el binario vía `api.getBlob`; acá se dispara la
 * descarga local con su nombre canónico sin abrir pestañas nuevas.
 */

/**
 * Fuerza la descarga de un Blob con un nombre de archivo. Sin DOM (SSR/tests) no hace nada.
 * @param {Blob} blob
 * @param {string} fileName
 */
export function saveReceiptBlob(blob, fileName) {
  if (!blob || typeof document === 'undefined' || typeof URL === 'undefined') return;

  const objectUrl = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = fileName || 'Comprobante.pdf';
    anchor.rel = 'noopener';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    // La revocación tardía evita cortar la descarga en curso.
    setTimeout(() => URL.revokeObjectURL(objectUrl), 30000);
  }
}

/** Nombre canónico del comprobante de venta a crédito (F + factura, o V + venta). */
export function creditSaleReceiptFileName(invoiceNumber, saleId) {
  const segment = Number(invoiceNumber) > 0
    ? `F${String(Number(invoiceNumber)).padStart(5, '0')}`
    : `V${saleId}`;
  return `Comprobante_Venta_Credito_${segment}.pdf`;
}

/** Nombre canónico del comprobante de abono. */
export function creditPaymentReceiptFileName(paymentId) {
  return `Comprobante_Abono_${paymentId}.pdf`;
}
