# Architecture — WPF (Desktop Client, Autenticación, Resiliencia, Flujos)

> Cliente WPF, patrón de resiliencia, autenticación y flujos de venta/caja.

---

## 3.2-3.3 Desktop Client (WPF .NET 10)

Dependencias: Backend API (HTTP + SignalR), `Desktop.Client.Core`

### Views (15+)

MainWindow, PosView, InventoryView, SalesHistoryView, PendingOrdersView, PendingPickupsView, SettingsView, ExchangeRateView, CashDrawerView, DailyClosureView, UsersManagementView, CustomerManagementView, BarcodeScannerWindow, ChangePasswordDialog, CashAdvanceDialog

### Desktop.Client.Core (MVVM + Resilience)

| Componente | Contenido |
|---|---|
| `ViewModels/` (30+) | MainViewModel, LoginViewModel, PosViewModel (+Scanning, +Orders), CheckoutViewModel (+Payments), InventoryViewModel (+Filters, +Operations), ProductDialogViewModel (+Pricing, +Variants), etc. |
| `Services/` | HealthPollingService, ResilienceHandler, UserSessionHeaderHandler, ClientStateService, CurrencyService, ProductService, SalesService, etc. |

### Patrón de Resiliencia

```
HttpClient
  └─ UserSessionHeaderHandler (X-User-Id, X-User-Role, X-Client-Version)
      └─ ResilienceHandler (retry + jitter, circuit breaker)
          └─ Backend API
```

---

## 8. Flujo de Autenticación

### Desktop (JWT + Headers)

```
LoginVM ──► AuthController ──► TokenService
  │                                │
  ◄── JWT Token ◄──────────────────┘
  │
  ▼ (cada petición)
UserSessionHeaderHandler ──► ResilienceHandler ──► Backend
```

### Web (JWT + Context)

```
LoginPage ──► AuthContext ──► Backend API
  │              │
  ◄── JWT ◄──────┘
  │
  ▼ (cada petición)
api.js (agrega Bearer token)
```

---

## 9. Flujo de Resiliencia (Desktop)

```
┌─────────────────────────────────────────────────────────┐
│                Cadena de DelegatingHandlers                │
├─────────────────────────────────────────────────────────┤
│  Request ──► UserSessionHeaderHandler ──► ResilienceHandler ──► API │
│                  │                          │                      │
│                  │ Agrega:                  │ Retry:              │
│                  │ • X-User-Id              │ • Exponential backoff│
│                  │ • X-User-Role            │ • Max 3 intentos    │
│                  │ • X-Client-Version       │ Circuit Breaker:    │
│                  │ • Authorization: Bearer  │ • 5 fallos → open 30s│
└─────────────────────────────────────────────────────────┘
```

---

## 14. Flujo de Venta Completa

```
1. POS Page → Agregar productos al carrito → Seleccionar cliente
   └─► Abrir CheckoutModal

2. CheckoutModal → Seleccionar métodos de pago
   └─► Generar/conservar Idempotency-Key UUID
       └─► Confirmar venta

3. Backend: POST /api/sales/{id}/complete
   ├─► Validar Idempotency-Key
   ├─► SalesService.CompleteSaleAsync()
   │   ├─► BeginTransaction (ReadCommitted)
   │   ├─► Deducción síncrona de stock
   │   ├─► Persistir Sale, Payments, CashTransactions
   │   ├─► Persistir OutboxMessage + IdempotentRequest
   │   └─► Commit atómico (Venta + Stock + Idempotencia)
   └─► Retornar InvoiceNumber

4. Response → SuccessScreen → Reset cart

5. Background: OutboxProcessorJob (SignalR) + IdempotencyCleanupJob
```

---

## 15. Flujo de Caja

```
APERTURA: RegisterPage/CashDrawerView → POST /api/cashdrawer/open
  └─► CashDrawerService.OpenSessionAsync()

MOVIMIENTOS: Ingresos/Egresos → POST /api/cashdrawer/transaction
  └─► CashDrawerService.AddTransactionAsync()

CIERRE: RegisterClosePage/DailyClosureView → POST /api/dailyclosure/close
  └─► DailyClosureService.CloseDayAsync()
      ├─► Calcular totales por método de pago
      └─► Crear DailyClosure + ClosureDetails
```

---

## 19. Integración de Hardware

### Escáneres de código de barras

- **WPF:** Pistolas USB/Bluetooth HID ("keyboard wedge"), capturadas por `KeyboardWedgeScannerListener`. Discrimina ráfagas de escáner (≤60ms) del tipeo humano.
- **Web:** `BarcodeScannerModal` con `@zxing/library` (cámara del dispositivo).

### Emparejamiento QR

- `QrCodeHelper` genera QR con ZXing codificando `https://IP:5001/?paired=true`.
- `SubnetScannerService` descubre backend en LAN.

### NO integrados

- Impresión térmica/fiscal (solo PDF)
- Balanzas, lectores de tarjeta, gaveta física, puertos serie
