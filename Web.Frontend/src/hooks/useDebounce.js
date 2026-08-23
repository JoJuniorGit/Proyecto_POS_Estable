import { useState, useEffect } from 'react';

/**
 * Hook de debounce para retrasar la actualización de un valor hasta que el usuario deje de escribir.
 * @param {any} value - El valor a retrasar.
 * @param {number} delay - El tiempo en milisegundos a esperar (por defecto 300ms).
 * @returns {any} El valor debounced.
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
