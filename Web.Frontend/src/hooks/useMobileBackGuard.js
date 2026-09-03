import { useState, useEffect, useRef } from 'react';

/**
 * useMobileBackGuard
 *
 * Hook para interceptar la navegación nativa de "Atrás" en navegadores móviles tanto con
 * botón físico como con gestos de deslizamiento desde los bordes (Android 10+ / iOS).
 *
 * Criterios:
 * 1. Manejo de Modales: Si hay un modal activo (cobro, búsqueda de cliente, escáner, variantes, etc.)
 *    y el usuario presiona "Atrás" o desliza el borde, el sistema intercepta el evento y solicita el cierre
 *    del modal manteniéndose en la vista del POS.
 * 2. Protección de Modales de Pago con Pagos Registrados: Si un modal de pago tiene al menos 1 pago registrado,
 *    el cierre se previene internamente y se solicita confirmación antes de descartar los pagos. Si el cierre
 *    es prevenido (retorna false), el guard re-sincroniza el historial automáticamente.
 * 3. Prevención de Salida con Carrito Activo: Si no hay modales abiertos pero hay productos en el carrito,
 *    el gesto/botón de retroceso activa un diálogo de confirmación personalizado del POS (ConfirmModal)
 *    sin depender del diálogo nativo window.confirm. Si el usuario desliza atrás de nuevo o pulsa cancelar,
 *    se cancela el diálogo y permanece en el POS con su carrito intacto.
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
  const isConfirmExitOpenRef = useRef(isConfirmExitOpen);

  activeModalRef.current = activeModal;
  onCloseModalRef.current = onCloseModal;
  hasItemsRef.current = hasItems;
  enabledRef.current = enabled;
  onConfirmExitRef.current = onConfirmExit;
  isConfirmExitOpenRef.current = isConfirmExitOpen;

  const modalPushedRef = useRef(false);
  const cartGuardPushedRef = useRef(false);
  const confirmExitPushedRef = useRef(false);
  const ignorePopstateRef = useRef(false);

  // 1. Entrada en el historial para modales activos
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

  // 2. Entrada en el historial para protección del carrito activo
  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || !window.history) return;

    if (hasItems) {
      if (!cartGuardPushedRef.current && !confirmExitPushedRef.current) {
        window.history.pushState({ isPosCartGuard: true }, '');
        cartGuardPushedRef.current = true;
      }
    } else {
      if (cartGuardPushedRef.current) {
        cartGuardPushedRef.current = false;
        ignorePopstateRef.current = true;
        window.history.back();
      }
    }
  }, [hasItems, enabled]);

  // 3. Listener central de popstate (botón y gestos de Android 10+) y beforeunload
  useEffect(() => {
    if (!enabled || typeof window === 'undefined' || !window.history) return;

    const handlePopState = () => {
      // Si la navegación hacia atrás fue ejecutada por nuestro propio código para limpiar una entrada, ignorar
      if (ignorePopstateRef.current) {
        ignorePopstateRef.current = false;
        return;
      }

      // Caso A: Si la confirmación personalizada de salida está abierta y el usuario vuelve a hacer
      // el gesto de deslizar atrás en Android: cancelar el diálogo y mantenerse en el POS
      if (isConfirmExitOpenRef.current) {
        confirmExitPushedRef.current = false;
        setIsConfirmExitOpen(false);
        // Re-establecer entrada de protección del carrito
        if (hasItemsRef.current) {
          window.history.pushState({ isPosCartGuard: true }, '');
          cartGuardPushedRef.current = true;
        }
        return;
      }

      // Caso B: Si hay un modal activo, el retroceso intenta cerrarlo
      if (activeModalRef.current) {
        modalPushedRef.current = false;
        const closeResult = onCloseModalRef.current?.();

        // Si el modal previno el cierre (ej. cobro con pagos registrados solicitando confirmación interna)
        if (closeResult === false) {
          // Re-insertar entrada de modal en el historial para sincronizar futuros gestos de Atrás
          window.history.pushState({ isPosModal: true }, '');
          modalPushedRef.current = true;
        }
        return;
      }

      // Caso C: Si no hay modales abiertos pero hay productos en el carrito
      if (hasItemsRef.current) {
        cartGuardPushedRef.current = false;
        // Abrir el modal de confirmación personalizado con diseño POS
        setIsConfirmExitOpen(true);
        // Insertar estado para que si el usuario hace otro gesto de Atrás en Android, cierre el diálogo
        window.history.pushState({ isConfirmExitModal: true }, '');
        confirmExitPushedRef.current = true;
        return;
      }
    };

    const handleBeforeUnload = (e) => {
      if (hasItemsRef.current) {
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

      if (confirmExitPushedRef.current) {
        confirmExitPushedRef.current = false;
        ignorePopstateRef.current = true;
        window.history.back();
      }
      if (modalPushedRef.current) {
        modalPushedRef.current = false;
        ignorePopstateRef.current = true;
        window.history.back();
      }
      if (cartGuardPushedRef.current) {
        cartGuardPushedRef.current = false;
        ignorePopstateRef.current = true;
        window.history.back();
      }
    };
  }, [enabled]);

  // Manejador cuando el usuario pulsa "Cancelar" en el modal de confirmación
  const handleCancelExit = () => {
    setIsConfirmExitOpen(false);
    if (confirmExitPushedRef.current) {
      confirmExitPushedRef.current = false;
      ignorePopstateRef.current = true;
      window.history.back();
    }
    // Restaurar guard del carrito
    if (hasItemsRef.current && !cartGuardPushedRef.current) {
      window.history.pushState({ isPosCartGuard: true }, '');
      cartGuardPushedRef.current = true;
    }
  };

  // Manejador cuando el usuario pulsa "Salir" en el modal de confirmación
  const handleConfirmExit = () => {
    setIsConfirmExitOpen(false);
    confirmExitPushedRef.current = false;
    cartGuardPushedRef.current = false;

    if (onConfirmExitRef.current) {
      onConfirmExitRef.current();
    } else {
      // Permitir la salida retrocediendo en el historial
      window.history.back();
    }
  };

  return {
    isConfirmExitOpen,
    handleCancelExit,
    handleConfirmExit,
  };
}
