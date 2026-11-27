export const SETTINGS_SECTIONS = Object.freeze([
  {
    id: 'currency',
    label: 'Moneda, Tasas y Formato',
    icon: 'currency',
    description: 'Formato, separadores y vista previa de montos.',
  },
  {
    id: 'payments',
    label: 'Métodos de Pago y Caja',
    icon: 'payments',
    description: 'Medios habilitados, comisiones y reglas de cobro.',
  },
  {
    id: 'network',
    label: 'Conectividad y Red Local',
    icon: 'network',
    description: 'Emparejamiento QR, direcciones IP y acceso local.',
  },
  {
    id: 'system',
    label: 'Sistema y Mantenimiento',
    icon: 'system',
    description: 'Reinicio del servicio y estado de la terminal.',
  },
]);

/**
 * Resolves the next tab index for the ARIA tablist keyboard contract.
 * Returns null for keys that are not tab-navigation keys or an invalid index.
 */
export function resolveTabIndex(currentIndex, key, count = SETTINGS_SECTIONS.length) {
  if (!Number.isInteger(currentIndex) || currentIndex < 0 || currentIndex >= count) {
    return null;
  }

  switch (key) {
    case 'ArrowRight':
    case 'ArrowDown':
      return (currentIndex + 1) % count;
    case 'ArrowLeft':
    case 'ArrowUp':
      return (currentIndex - 1 + count) % count;
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return null;
  }
}
