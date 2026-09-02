import { useEffect, useRef } from 'react';
import { isValidBarcode } from '../utils/barcodeValidator';

/**
 * Hook para captura global no intrusiva de pistolas lectoras de código de barras físicas (USB / Bluetooth HID).
 * Discrimina entre ráfagas ultrarrápidas de escáner (<= 60ms entre teclas) y tipeo humano.
 * @param {(code: string) => void | Promise<void>} onScan - Callback al detectar un código válido.
 * @param {number} [maxInterKeyIntervalMs=60] - Umbral máximo entre caracteres.
 */
export function useScannerTrap(onScan, maxInterKeyIntervalMs = 60) {
  const onScanRef = useRef(onScan);
  const bufferRef = useRef('');
  const lastKeyTimeRef = useRef(0);

  useEffect(() => {
    onScanRef.current = onScan;
  }, [onScan]);

  useEffect(() => {
    const handleKeyDown = (e) => {
      // Si el usuario presiona Escape, se limpia el buffer
      if (e.key === 'Escape') {
        bufferRef.current = '';
        lastKeyTimeRef.current = 0;
        return;
      }

      // Protección de foco: si el usuario está tipeando en un input o textarea editable
      // (por ejemplo: notas, nombre de cliente, monto manual) que no sea el buscador POS,
      // no interceptamos la entrada.
      const target = e.target;
      const isInput = target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement;
      const isPosSearch = target?.classList?.contains('pos-search-input') || target?.getAttribute('data-scanner-trap') === 'true';

      if (isInput && !isPosSearch) {
        bufferRef.current = '';
        lastKeyTimeRef.current = 0;
        return;
      }

      const now = e.timeStamp || Date.now();
      const delta = now - lastKeyTimeRef.current;
      lastKeyTimeRef.current = now;

      // Si es Enter (fin de código enviado por la pistola)
      if (e.key === 'Enter') {
        const candidate = bufferRef.current.trim();
        bufferRef.current = '';
        lastKeyTimeRef.current = 0;

        if (candidate.length >= 4 && isValidBarcode(candidate)) {
          e.preventDefault();
          e.stopPropagation();
          onScanRef.current?.(candidate);
        }
        return;
      }

      // Si es una sola tecla de carácter imprimible
      if (e.key && e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
        // Si el tiempo entre teclas supera el umbral, se reinicia el buffer (tipeo humano lento)
        if (bufferRef.current.length > 0 && delta > maxInterKeyIntervalMs) {
          bufferRef.current = '';
        }

        bufferRef.current += e.key;
      }
    };

    window.addEventListener('keydown', handleKeyDown, { capture: true });

    return () => {
      window.removeEventListener('keydown', handleKeyDown, { capture: true });
      bufferRef.current = '';
      lastKeyTimeRef.current = 0;
    };
  }, [maxInterKeyIntervalMs]);
}
