# Arquitectura del Frontend React (POS Web SPA)

Este documento describe las pautas arquitectónicas, el manejo de contexto de alta frecuencia de renderizado y el ciclo de vida del hardware de cámara en el cliente Web del sistema POS (React 19 + Vite).

---

## 1. Patrón de Contextos Divididos (Split Context Pattern)

Para prevenir micro-congelamientos de la interfaz durante escaneos rápidos y tecleos en el punto de venta, la gestión del carrito de compras está dividida en dos contextos independientes:

```
                  ┌─────────────────────────────────┐
                  │          CartProvider           │
                  └────────────────┬────────────────┘
                                   │
          ┌────────────────────────┴────────────────────────┐
          ▼                                                 ▼
┌───────────────────┐                             ┌───────────────────┐
│ CartStateContext  │                             │CartActionsContext │
│ (Datos Reactivos) │                             │(Callbacks Estables│
└─────────┬─────────┘                             └─────────┬─────────┘
          │                                                 │
          ▼                                                 ▼
┌───────────────────┐                             ┌───────────────────┐
│  useCartState()   │                             │  useCartActions() │
│ (CartTable, Totals│                             │ (Action Buttons,  │
│  SummaryPanel)    │                             │  usePosHotkeys)   │
└───────────────────┘                             └───────────────────┘
```

### Guía de Consumo:
- **`useCartState()`**: Consumir en componentes que **muestran** datos reactivos del carrito (ej: `CartTable`, `CartList`, `SummaryPanel`, `EmptyCart`).
- **`useCartActions()`**: Consumir en componentes o hooks que solo **disparan** acciones sin necesitar leer el carrito completo (ej: `usePosHotkeys`, botones de limpiar carrito, cambiar lista de precios). Sus referencias son estables mediante `useCallback` y `useMemo`.
- **`useCart()`**: Mantiene retrocompatibilidad agregando tanto estado como acciones (utilizar únicamente en controladores globales cuando sea indispensable).

---

## 2. Gestión de Modales (`usePosModalFlow`)

El hook `usePosModalFlow` centraliza el estado de visibilidad de modales en el POS (`activeModal`), aplicando la regla de aislamiento de modales para prevenir conflictos entre atajos de teclado y captura de barra.

```js
const { activeModal, openCustomerModal, closeAllModals } = usePosModalFlow();
```

---

## 3. Ciclo de Vida de Cámara (`useCameraHardwareLifecycle`)

El hook `useCameraHardwareLifecycle` desacopla la UI de los detalles de hardware:
- Aceleración por GPU nativa con `window.BarcodeDetector` (3-8ms) en móviles/tablets.
- Decodificación con software vision pipeline (`Sauvola`, `1D Sharpen`, `Inversion`) en laptops/desktops con ZXing.
- Encendido/apagado atómico de `MediaStreamTrack`.
- Cancelación reactiva con `sessionCancelTokenRef`.
- Liberación explícita de `AudioContext` vía `closeAudioContext()`.
