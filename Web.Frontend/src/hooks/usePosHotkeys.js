import { useEffect, useRef } from 'react';

/**
 * Hook global para manejo y homologación de atajos de teclado POS estándar.
 *
 * Características:
 * 1. Intercepta y bloquea atajos predeterminados del navegador (F1 Ayuda, F3 Buscar, F5 Recargar, etc.).
 * 2. Aplica gating estricto si hay un modal activo (`activeModal` / `isAnyModalOpen`).
 * 3. Protege las teclas de manipulación de carrito (+, -, Delete) cuando el foco está en un campo de texto editable.
 * 4. Soporta teclas numéricas estándar y del teclado numérico (NumpadAdd, NumpadSubtract).
 */
export function usePosHotkeys({
  activeModal = null,
  onCloseActiveModal,
  onEscapeBackground,
  onCheckout,
  onSearchFocus,
  onChangeCustomer,
  onHold,
  onSyncRate,
  onCashAdvance,
  onTogglePriceList,
  onClearCart,
  onDeleteItem,
  onIncreaseQuantity,
  onDecreaseQuantity,
  enabled = true,
}) {
  // Mantener referencias actualizadas para evitar recrear listeners innecesariamente
  const handlersRef = useRef({
    activeModal,
    onCloseActiveModal,
    onEscapeBackground,
    onCheckout,
    onSearchFocus,
    onChangeCustomer,
    onHold,
    onSyncRate,
    onCashAdvance,
    onTogglePriceList,
    onClearCart,
    onDeleteItem,
    onIncreaseQuantity,
    onDecreaseQuantity,
    enabled,
  });

  useEffect(() => {
    handlersRef.current = {
      activeModal,
      onCloseActiveModal,
      onEscapeBackground,
      onCheckout,
      onSearchFocus,
      onChangeCustomer,
      onHold,
      onSyncRate,
      onCashAdvance,
      onTogglePriceList,
      onClearCart,
      onDeleteItem,
      onIncreaseQuantity,
      onDecreaseQuantity,
      enabled,
    };
  }, [
    activeModal,
    onCloseActiveModal,
    onEscapeBackground,
    onCheckout,
    onSearchFocus,
    onChangeCustomer,
    onHold,
    onSyncRate,
    onCashAdvance,
    onTogglePriceList,
    onClearCart,
    onDeleteItem,
    onIncreaseQuantity,
    onDecreaseQuantity,
    enabled,
  ]);

  useEffect(() => {
    const handleKeyDown = (e) => {
      const h = handlersRef.current;
      if (!h.enabled) return;

      const isAnyModalOpen = Boolean(h.activeModal);
      const activeTag = document.activeElement?.tagName;
      const isEditingInput =
        ['INPUT', 'TEXTAREA', 'SELECT'].includes(activeTag) ||
        Boolean(document.activeElement?.isContentEditable);

      // 1. Manejo jerárquico de tecla ESCAPE
      if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        if (isAnyModalOpen) {
          h.onCloseActiveModal?.();
        } else {
          h.onEscapeBackground?.();
        }
        return;
      }

      // 2. Intercepción de teclas de función F1 - F12
      const isFunctionKey =
        e.key.length >= 2 &&
        e.key.startsWith('F') &&
        !isNaN(Number(e.key.substring(1)));

      if (isFunctionKey) {
        // Bloquear SIEMPRE el comportamiento nativo del navegador en pantalla POS
        e.preventDefault();
        e.stopPropagation();

        // Si hay un modal abierto, bloquear todas las acciones globales de fondo
        if (isAnyModalOpen) return;

        switch (e.key) {
          case 'F1':
            h.onCheckout?.();
            break;
          case 'F2':
            h.onSearchFocus?.();
            break;
          case 'F3':
            h.onChangeCustomer?.();
            break;
          case 'F4':
            h.onHold?.();
            break;
          case 'F5':
            h.onSyncRate?.();
            break;
          case 'F6':
            h.onCashAdvance?.();
            break;
          case 'F7':
            h.onTogglePriceList?.();
            break;
          case 'F8':
            h.onClearCart?.();
            break;
          default:
            break;
        }
        return;
      }

      // 3. Teclas de manipulación de ítems del carrito (+, -, Delete)
      // Solo se activan si NO hay ningún modal abierto y el foco NO está dentro de un campo de texto editable
      if (!isAnyModalOpen && !isEditingInput) {
        // Delete / Supr
        if (e.key === 'Delete' || e.key === 'Del') {
          e.preventDefault();
          h.onDeleteItem?.();
          return;
        }

        // Suma (+)
        if (e.key === '+' || e.key === 'Add' || e.code === 'NumpadAdd') {
          e.preventDefault();
          h.onIncreaseQuantity?.();
          return;
        }

        // Resta (-)
        if (e.key === '-' || e.key === 'Subtract' || e.code === 'NumpadSubtract') {
          e.preventDefault();
          h.onDecreaseQuantity?.();
          return;
        }
      }
    };

    // Usar 'capture: true' para interceptar antes de que el navegador o componentes hijos capturen el evento
    window.addEventListener('keydown', handleKeyDown, true);
    return () => window.removeEventListener('keydown', handleKeyDown, true);
  }, []);
}
