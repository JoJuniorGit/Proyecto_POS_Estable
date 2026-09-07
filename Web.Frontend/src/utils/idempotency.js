// 8.2-CR1 / 8.6-B4: CICLO DE VIDA de la Idempotency-Key por intento de cobro.
// La clave es ESTABLE mientras dura el intento (los reenvíos por red comparten la MISMA clave),
// y se RESETEA al descartar los pagos o al liquidar la venta para forzar una clave nueva.
// Centralizado en un módulo puro para poder probar su comportamiento a nivel de objeto
// (antes se sondeaba el fuente con fs.readFileSync, ver 8.6-B4).

export function createCheckoutKeyHolder(getUuid = null) {
  let key = null;

  const generate = () => {
    const uuidGen = getUuid
      || (typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID.bind(crypto) : null);
    return uuidGen ? uuidGen() : `checkout-${Date.now()}-${Math.random().toString(36).substring(2, 9)}`;
  };

  return {
    getOrCreateKey() {
      if (!key) key = generate();
      return key;
    },
    reset() {
      key = null;
    },
  };
}