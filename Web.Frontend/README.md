# Web Frontend — Sistema POS Administrador

Cliente web del Sistema POS construido con React 19 + Vite 8, diseñado para cajas, tablets y dispositivos móviles conectados por red local al Backend API.

---

## Stack Tecnológico

| Tecnología | Versión | Uso |
|---|---|---|
| React | ^19.2.7 | UI framework |
| Vite | ^8.1.1 | Bundler + dev server |
| @microsoft/signalr | ^10.0.5 | Actualizaciones de tasa de cambio en tiempo real |
| @zxing/library | ^0.23.0 | Escaneo de código de barras (fallback por software) |
| lucide-react | ^1.27.0 | Iconografía |
| oxlint | ^1.71.0 | Linter (0 warnings en CI) |

---

## Estructura del Proyecto

```
src/
├── pages/              # 11 páginas (Login, POS, Catalog, History, etc.)
├── components/         # 34 componentes reutilizables
├── context/            # Contextos React (Auth, ExchangeRate, Cart)
├── hooks/              # Custom hooks (usePosHotkeys, useAtmKeypad, etc.)
├── services/           # Capa HTTP (api.js, salesApi.js, etc.)
├── utils/              # Utilidades (formatters.js, parseAmount, etc.)
├── App.jsx             # Enrutador principal
├── main.jsx            # Punto de entrada
└── index.css           # Estilos globales con design tokens CSS
test/
└── esbuild-jsx-loader.mjs  # Cargador JSX para node --test
```

---

## Scripts Disponibles

```bash
npm run dev       # Servidor de desarrollo con HMR (--host para acceso LAN)
npm run build     # Build de producción (output: dist/)
npm run test      # Suite de pruebas con node:test + esbuild JSX loader
npm run lint      # Linting con oxlint
npm run preview   # Preview del build de producción
```

---

## Arquitectura

### Contextos (Estado Global)
- **AuthContext** — Autenticación JWT, login/logout, token en memoria.
- **ExchangeRateContext** — Tasa BCV en tiempo real vía SignalR (`ExchangeRateHub`).
- **CartContext** — Carrito de compras con Split Context Pattern:
  - `CartStateContext` (datos) + `CartActionsContext` (mutaciones) para evitar re-renders innecesarios.

### Comunicación con el Backend
- HTTP REST via `api.js` (agrega Bearer token automáticamente).
- SignalR para actualizaciones de tasa de cambio y eventos de venta.
- El build de producción (`npm run build`) se copia a `Backend.API/wwwroot/` para servirse como SPA estática.

### Convenciones
- **Cero estilos inline** — Usar design tokens CSS (`var(--bg-surface)`, `var(--text-primary)`, etc.).
- **DTOs en camelCase** — Alineados con el contrato JSON del Backend.
- **Formateo monetario centralizado** — `formatAmount`, `formatBsS`, `formatUSD`, `parseAmount` desde `utils/formatters.js`.

---

## Testing

Las pruebas se ejecutan con el test runner nativo de Node.js (`node:test`) con cargador JSX vía esbuild:

```bash
npm test
```

Archivos de test siguen el patrón `*.test.js` dentro de `src/`.

---

## Documentación Relacionada

- [Arquitectura React detallada](../docs/react-architecture.md) — Split Context Pattern, modales, hardware de cámara.
- [Directrices de código](../docs/coding-guidelines.md) — Convenciones React 19, design tokens, prohibiciones.
- [Arquitectura general del sistema](../ARCHITECTURE.md) — Visión completa del POS.
