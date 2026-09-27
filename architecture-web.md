# Architecture — Web (React, Flujos de Eventos, Multi-Format)

> Cliente web React, flujos de eventos MediatR/SignalR y motor multi-formato monetario.

---

## 3.1 Web Frontend (React 19 + Vite 8)

Dependencias: Backend API (HTTP + SignalR)

| Categoría | Contenido |
|---|---|
| **Pages** (10) | LoginPage, PosPage, CatalogPage, HistoryPage, PendingOrdersPage, PendingPickupsPage, RegisterPage, RegisterClosePage, SettingsPage, ExchangeRatePage |
| **Components** (36) | Layout, Cart, ProductGrid, ProductSearch, CustomerSelector, BarcodeScannerModal, CheckoutModal, HoldSaleModal, SuccessScreen, ATMInput, VariantSelectorModal, etc. |
| **Context** (3) | AuthContext, ExchangeRateContext, CartContext |
| **Services** | api.js (productService, salesService, cashDrawerService, etc.) |

**Dependencias npm:**
- `@microsoft/signalr` ^10.0.5
- `@zxing/library` ^0.23.0
- `lucide-react` ^1.27.0
- `react` ^19.2.7

---

## 4. Flujos de Eventos

### 4.1 SaleMadeEvent (MediatR)

```
SalesService ──► MediatR Bus ──► InventorySaleMadeEventHandler
(crea venta)                       (descuenta stock)
```

- Handler descuenta stock con retry (3 intentos)
- Ítems `IsCashAdvance` se excluyen de deducción física

### 4.2 ExchangeRateHub (SignalR)

**Modelo Híbrido:**
- **Automático:** `BcvExchangeRateJob` cada 2 horas
- **Manual:** Admin sincroniza con `POST /api/exchange-rate/sync-bcv` o tecla F5
- **Fallback:** Si no hay cotización del día, usa último registro histórico válido

**Propagación:** Cambio → persiste en `ExchangeRateHistory` → purga caché → recalcula ventas en espera → emite `ReceiveRateUpdate` + `OnHoldSalesUpdated` vía SignalR.

### 4.3 HealthPolling (Desktop)

```
HealthPollingService ──► GET /health cada 3s ──► ClientStateService
```

### 4.4 VersionCheck

```
VersionCheckService ──► GET /api/version ──► {isCompatible, minVersion, updateUrl}
    │ (si incompatible)
    ▼
VersionLockoutDialog → Shutdown()
```

### 4.5 Motor Multi-Formato Monetario

Dos estándares numéricos gobernados globalmente:

1. **Venezolano Contable:** separador miles `.`, decimal `,` → `172.786,94`
2. **Internacional:** separador miles `,`, decimal `.` → `172,786.94`

**Regla de Parseo Universal (`parseAmount`):**
- Un solo separador → decimal
- Dos+ separadores → último es decimal, precedentes son miles

**Utilidades:** `formatAmount`, `formatBsS`, `formatUSD`, `parseAmount` (Web) / `NumericCalculatorBehavior.ParseAmount` (WPF).
