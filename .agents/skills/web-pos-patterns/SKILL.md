---
name: web-pos-patterns
description: >-
  Standardizes React 19 + Vite web client patterns for the POS application. Covers POS keyboard hotkey gating
  (usePosHotkeys), real-time monetary formatting (useAtmKeypad), network reconnect polling, modal isolation,
  and Vanilla CSS design tokens (var(--bg-surface), var(--text-primary), var(--border), var(--bg-hover))
  aligned with Desktop Material Design. Activate this skill when building or editing React components,
  web checkout modals, ATM numeric keypads, or touch/mobile responsive views.
---

# Web POS Patterns & React Standards Guide

This skill governs the web client development in `React 19 + Vite + Vanilla CSS Custom Properties`, ensuring instant responsiveness, touchscreen compatibility, and 100% behavioral parity with the Desktop WPF application.

---

## 1. POS Hotkey Hook with Modal Gating (`usePosHotkeys.js`)

**Rule**: Global hotkeys must be suppressed when typing inside inputs/textareas and gated when modal dialogs are open:

```javascript
import { useEffect } from 'react';

/**
 * Hook global de atajos de teclado POS con gating de modales y protección de inputs.
 */
export function usePosHotkeys({
  onCheckout,
  onFocusSearch,
  onHoldOrder,
  onClearSale,
  isModalOpen = false,
  onCloseModal
}) {
  useEffect(() => {
    const handleKeyDown = (e) => {
      const isInputFocused = ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);

      // ESC: Cierra el modal activo si existe, de lo contrario no propaga
      if (e.key === 'Escape') {
        if (isModalOpen && onCloseModal) {
          e.preventDefault();
          onCloseModal();
          return;
        }
      }

      // Si hay un modal abierto, bloquear atajos globales de fondo
      if (isModalOpen) return;

      // F1: Cobrar / Checkout
      if (e.key === 'F1') {
        e.preventDefault();
        onCheckout?.();
      }
      // F2: Enfocar buscador
      else if (e.key === 'F2') {
        e.preventDefault();
        onFocusSearch?.();
      }
      // F4: Poner pedido en espera
      else if (e.key === 'F4') {
        e.preventDefault();
        onHoldOrder?.();
      }
      // F8: Limpiar venta
      else if (e.key === 'F8' && !isInputFocused) {
        e.preventDefault();
        onClearSale?.();
      }
    };

    window.addEventListener('keydown', handleKeyDown, { capture: true });
    return () => window.removeEventListener('keydown', handleKeyDown, { capture: true });
  }, [onCheckout, onFocusSearch, onHoldOrder, onClearSale, isModalOpen, onCloseModal]);
}
```

---

## 2. ATM Numeric Keypad Hook (`useAtmKeypad.js`)

For high-speed cash entry where typing digits fills from cents to units (e.g. typing `1`, `0`, `0` produces `1.00`):

```javascript
import { useState, useCallback } from 'react';

export function useAtmKeypad(initialValue = 0) {
  const [cents, setCents] = useState(Math.round(initialValue * 100));

  const appendDigit = useCallback((digit) => {
    setCents((prev) => {
      const next = prev * 10 + digit;
      return next <= 99999999 ? next : prev; // Limit max amount
    });
  }, []);

  const backspace = useCallback(() => {
    setCents((prev) => Math.floor(prev / 10));
  }, []);

  const clear = useCallback(() => {
    setCents(0);
  }, []);

  const amount = cents / 100;
  const formatted = amount.toLocaleString('es-VE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return { amount, formatted, appendDigit, backspace, clear };
}
```

---

## 3. Vanilla CSS Design Tokens (WPF Material Parity)

* **Strict Rule**: Never introduce Tailwind or third-party CSS utility libraries. Use the project's native CSS custom properties defined in `Web.Frontend/src/index.css`:

```css
/* Tokens Oficiales Extraídos de WPF Desktop */
:root {
  --bg-primary: #F8FAFC;
  --bg-surface: #FFFFFF;
  --bg-hover: #F1F5F9;
  --border: #CBD5E1;
  --text-primary: #0F172A;
  --text-muted: #64748B;
  --primary: #2563EB;
  --primary-hover: #1D4ED8;
  --success: #16A34A;
  --warning: #D97706;
  --danger: #DC2626;
  --radius-sm: 6px;
  --radius-md: 10px;
  --radius-lg: 16px;
}

/* Ejemplo de Tarjeta de Componente */
.pos-card {
  background-color: var(--bg-surface);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  padding: 16px;
  transition: background-color 0.15s ease, border-color 0.15s ease;
}

.pos-card:hover {
  background-color: var(--bg-hover);
}
```

---

## 4. Network Reconnect & Health Polling

Handle backend reconnection cleanly without full page refreshes:

```javascript
import { useEffect, useState } from 'react';

export function useNetworkStatus(checkIntervalMs = 5000) {
  const [isOnline, setIsOnline] = useState(true);

  useEffect(() => {
    const checkHealth = async () => {
      try {
        const res = await fetch('/api/health', { method: 'GET', cache: 'no-store' });
        setIsOnline(res.ok);
      } catch {
        setIsOnline(false);
      }
    };

    const interval = setInterval(checkHealth, checkIntervalMs);
    return () => clearInterval(interval);
  }, [checkIntervalMs]);

  return { isOnline };
}
```
