import { useRef, useCallback, useEffect } from 'react';
import { closeAudioContext } from '../utils/soundEffects';

export function useCameraHardwareLifecycle() {
  const activeStreamRef = useRef(null);
  const sessionCancelTokenRef = useRef(0);
  const zxingReaderRef = useRef(null);

  const stopActiveStream = useCallback(() => {
    sessionCancelTokenRef.current += 1;
    if (zxingReaderRef.current) {
      try {
        zxingReaderRef.current.reset();
      } catch {
        // Ignorar excepciones al reiniciar ZXing
      }
    }
    if (activeStreamRef.current) {
      activeStreamRef.current.getTracks().forEach((track) => {
        try {
          track.stop();
        } catch {
          // Ignorar excepciones al detener pistas
        }
      });
      activeStreamRef.current = null;
    }
  }, []);

  useEffect(() => {
    return () => {
      stopActiveStream();
      closeAudioContext();
    };
  }, [stopActiveStream]);

  return {
    activeStreamRef,
    sessionCancelTokenRef,
    zxingReaderRef,
    stopActiveStream,
  };
}
