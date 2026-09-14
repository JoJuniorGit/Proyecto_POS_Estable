# Coding Guidelines — Frontend Web (React 19 / JSX / Vite)

> Sección web. Aplica a cambios en `Web.Frontend`.

---

## 3.1. Estructura de Componentes y Nombres

* **Archivos y Módulos:**
  * Componentes y páginas: `PascalCase.jsx` (`QuantityInput.jsx`, `PaymentModal.jsx`).
  * Custom Hooks: `camelCase.js` con prefijo `use` (`usePosHotkeys.js`).
  * Clientes API y utilidades: `camelCase.js` (`api.js`, `formatters.js`).
  * Constantes: `UPPER_SNAKE_CASE` (`DEFAULT_TIMEOUT_MS`).

* **Orden Interno en Componentes:**
  1. Imports externos (React, librerías).
  2. Imports internos (componentes UI, contextos, utilidades).
  3. Constantes del archivo / helpers puros.
  4. Declaración del componente.
  5. Hooks de estado y referencias.
  6. Efectos y callbacks.
  7. Handlers de eventos.
  8. Render JSX.

## 3.2. Contrato de DTOs

* Los DTOs serializan en **`camelCase`**. En React: siempre `item.isFractional`, nunca `item.IsFractional`.
* Errores vía `api.js` centralizado (deserializa `ProblemDetails` jerárquicamente).

## 3.3. Estilos y Tokens de Diseño

* **Cero Estilos Inline** para valores fijos.
* **Design Tokens (Variables CSS):**
  * Fondos: `var(--bg-surface)`, `var(--bg-card)`, `var(--bg-hover)`, `var(--bg-input)`.
  * Textos: `var(--text-primary)`, `var(--text-secondary)`, `var(--text-muted)`.
  * Bordes: `var(--border)`, `var(--border-focus)`.
  * Estados: `var(--color-primary)`, `var(--color-success)`, `var(--color-danger)`.
