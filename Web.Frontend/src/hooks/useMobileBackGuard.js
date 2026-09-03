import { useState, useEffect, useRef } from 'react';

/**
 * useMobileBackGuard
 *
 * Hook para interceptar la navegación nativa de "Atrás" en navegadores móviles tanto con
 * botón físico como con gestos de deslizamiento desde los bordes (Android 10+ / iOS).
 *
 * Protección Inquebrantable contra Intentos Consecutivos:
 * - Mientras haya productos en el carrito (hasItems = true), NINGÚN intento de retroceso
 *   (sea el primero, segundo o décimo consecutivo) permitirá la salida involuntaria del sistema.
 * - Cada intento de retroceso re-inyecta inmediatamente la entrada de protección en el historial
 *   (history.pushState) y mantiene en pantalla el diálogo de confirmación personalizado del POS.
 * - La única vía para salir del sistema es pulsar explícitamente el botón "Salir del Sistema",
 *   el cual activa allowExitRef y permite la navegación limpia hacia atrás.
 */
export function useMobileBackGuard({
  activeModal,
  onCloseModal,
  hasItems = false,
  enabled = true,
  onConfirmExit = null,
}) {
  const [isConfirmExitOpen, setIsConfirmExitOpen] = useState(false);

  const activeModalRef = useRef(activeModal);
  const onCloseModalRef = useRef(onCloseModal);
  const hasItemsRef = useRef(hasItems);
  const enabledRef = useRef(enabled);
  const onConfirmExitRef = useRef(onConfirmExit);
  const isConfirmExitOpenRef = useRef(false);
  const allowExitRef = useRef(false);

  activeModalRef.current = activeModal;
  onCloseModalRef.current = onCloseModal;
  hasItemsRef.current = hasItems;
  enabledRef.current = enabled;
  onConfirmExitRef.current = onConfirmExit;

  const modalPushedRef = useRef(false);
  const cartGuardedRef = useRef(false);
  const ignorePopstateRef = useRef(false);

  // Helper síncrono para abrir confirmación
  const showExitConfirmation = () => {
    isConfirmExitOpenRef.current = true;
    setIsConfirmExitOpen(true);
  };

  // Helper síncrono para cancelar confirmación y permanecer en el POS
  const handleCancelExit = () => {
    isConfirmExitOpenRef.current = false;
    setIsConfirmExitOpen(false);
    // Asegurar que el guard del historial se mantenga activo
    if (hasItemsRef.current && enabledRef.current && typeof window !== 'undefined') {
      window.history.pushState({ isPosGuard: true }, '');
      cartGuardedRef.current = true;
    }
  };

  // Helper para confirmar salida definitiva del sistema
  const handleConfirmExit = () => {
    allowExitRef.current = true;
    isConfirmExitOpenRef.current = false;
    setIsConfirmExitOpen(false);

    if (onConfirmExitRef.current) {
      onConfirmExitRef.current();
    } else if (typeof window !== 'undefined') {
      window.history.back();
    }
  };

  // 1. Sincronización del historial para modales activos
  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || !window.history) return;

    if (activeModal) {
      if (!modalPushedRef.current) {
        window.history.pushState({ isPosModal: true }, '');
        modalPushedRef.current = true;
      }
    } else {
      if (modalPushedRef.current) {
        modalPushedRef.current = false;
        ignorePopstateRef.current = true;
        window.history.back();
      }
    }
  }, [activeModal, enabled]);

  // 2. Colchón de protección del carrito activo
  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || !window.history) return;

    if (hasItems) {
      if (!cartGuardedRef.current) {
        window.history.pushState({ isPosGuard: true }, '');
        cartGuardedRef.current = true;
      }
    } else {
      if (cartGuardedRef.current) {
        cartGuardedRef.current = false;
        if (!allowExitRef.current) {
          ignorePopstateRef.current = true;
          window.history.back();
        }
      }
    }
  }, [hasItems, enabled]);

  // 3. Listener central de popstate y beforeunload
  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || !window.history) return;

    const handlePopState = () => {
      // Si se autorizó la salida explícitamente desde "Salir del Sistema", permitir navegación
      if (allowExitRef.current) {
        return;
      }

      // Si la navegación hacia atrás fue ejecutada por nuestro propio código para limpiar una entrada, ignorar
      if (ignorePopstateRef.current) {
        ignorePopstateRef.current = false;
        return;
      }

      // Caso A: Si hay un modal activo, el retroceso intenta cerrarlo
      if (activeModalRef.current) {
        modalPushedRef.current = false;
        const closeResult = onCloseModalRef.current?.();

        // Si el modal previno el cierre (ej. cobro con pagos registrados solicitando confirmación interna)
        if (closeResult === false) {
          // Re-insertar entrada de modal en el historial para mantener la protección activa
          window.history.pushState({ isPosModal: true }, '');
          modalPushedRef.current = true;
        }
        return;
      }

      // Caso B: Si el carrito tiene productos (con o sin el diálogo de confirmación ya abierto)
      if (hasItemsRef.current) {
        // Barrera inquebrantable contra intentos consecutivos:
        // Cada intento de retroceso re-inyecta inmediatamente la protección en el historial
        window.history.pushState({ isPosGuard: true }, '');
        cartGuardedRef.current = true;

        // Mantener/mostrar la confirmación personalizada del POS
        showExitConfirmation();
        return;
      }
    };

    const handleBeforeUnload = (e) => {
      if (hasItemsRef.current && !allowExitRef.current) {
        const msg = '¿Seguro que desea abandonar la página? Se perderá el carrito actual';
        e.preventDefault();
        e.returnValue = msg;
        return msg;
      }
    };

    window.addEventListener('popstate', handlePopState);
    window.addEventListener('beforeunload', handleBeforeUnload);

    return () => {
      window.removeEventListener('popstate', handlePopState);
      window.removeEventListener('beforeunload', handleBeforeUnload);
    };
  }, [enabled]);

  return {
    isConfirmExitOpen,
    handleCancelExit,
    handleConfirmExit,
  };
}
