import { useState, useEffect } from 'react';

/**
 * Hook reutilizable para debounce de valores (300ms por defecto).
 * Retrasa la actualización del valor devuelto hasta que pase el tiempo especificado sin cambios.
 *
 * @param {any} value - El valor a retrasar.
 * @param {number} delay - Tiempo en milisegundos (por defecto 300ms).
 * @returns {any} El valor con debounce aplicado.
 */
export function useDebounce(value, delay = 300) {
  const [debouncedValue, setDebouncedValue] = useState(value);

  useEffect(() => {
    const handler = setTimeout(() => {
      setDebouncedValue(value);
    }, delay);

    return () => {
      clearTimeout(handler);
    };
  }, [value, delay]);

  return debouncedValue;
}

export default useDebounce;
