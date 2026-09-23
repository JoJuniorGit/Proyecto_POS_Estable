# Architecture — Backend (Módulos, Persistencia, Middleware, Seguridad)

> Módulos del backend, contextos de datos, pipeline de middleware y seguridad.

---

## 2. Módulos del Backend

### 2.1 `Core` — Librería Compartida

Dependencias: **Ninguna** (raíz del grafo).

| Componente | Contenido |
|---|---|
| `Entities/` | BaseEntity, Customer, ExchangeRateHistory, Order, Product, StockMovement, StockReservation, SystemSetting, User |
| `DTOs/` | BulkImportRequestDto, CustomerDto, PagedResultDto, ProductDto, SaleDto, UserDto |
| `Interfaces/` | ICurrentUserService, IInventoryService, ISystemSettingsService |
| `Events/` | SaleMadeEvent (record) |
| `Helpers/` | PricingCalculator, TimeZoneHelper |

**Entidades principales:**
- `User` — Cédula, Username, PasswordHash, Role, MustChangePassword
- `Product` — SKU, precios USD/Bs.S, stock, IsCashAdvance
- `Sale` — InvoiceNumber, TotalUSD, TotalBsS, AppliedRate, ClaimedByUserId/ClaimAction/ClaimedAtUtc
- `CashDrawerSession` — OpeningBalanceLocal, ClosingBalanceLocal, UserId

### 2.2 `Sales.Module`

Dependencias: `Core`

| Componente | Contenido |
|---|---|
| `Services/` | SalesService (6 partials: Pricing, HoldOrders, HoldClaims, CashAdvance, History), CashDrawerService, DailyClosureService |
| `Data/` | SalesDbContext |
| `Migrations/` | 35 archivos |

### 2.3 `Inventory.Module`

Dependencias: `Core`

| Componente | Contenido |
|---|---|
| `Services/` | InventoryService (7 partials: ExchangeRate, StockDeduction, CatalogQueries, Import, Export, ProductCrud) |
| `Data/` | InventoryDbContext |
| `EventHandlers/` | InventorySaleMadeEventHandler |

### 2.4 `Backend.API`

Dependencias: Core, Sales.Module, Inventory.Module, MediatR

| Componente | Contenido |
|---|---|
| `Controllers/` | 14 controladores REST |
| `Hubs/` | ExchangeRateHub (SignalR) |
| `Jobs/` | 5 BackgroundServices (BCV, Reservas, Stock, Outbox, Idempotencia) |
| `Middleware/` | GlobalExceptionHandler, VersionCheck, SecurityHeaders |

---

## 5. Contextos de Datos

### 5.1 InventoryDbContext

| DbSet | Descripción |
|---|---|
| `Products` | Catálogo |
| `StockMovements` | Historial de movimientos |
| `StockMovements_Archive` | Archivado histórico |
| `StockReservations` | Reservas para pedidos |
| `SystemSettings` | Config key-value |
| `ExchangeRateHistory` | Historial de tasas (1/día) |

### 5.2 SalesDbContext

| DbSet | Descripción |
|---|---|
| `Users` | Usuarios (Admin, Cashier, Driver) |
| `Customers` | Clientes |
| `Sales` | Cabeceras de ventas |
| `SaleItems` | Detalle de ventas |
| `SalePayments` | Pagos |
| `CashDrawerSessions` | Sesiones de caja |
| `CashTransactions` | Movimientos de efectivo |
| `DailyClosures` | Cierres diarios |
| `OutboxMessages` | Mensajes SignalR |
| `IdempotentRequests` | Registro forense de idempotencia |

### 5.3 Relaciones

Ambos contextos apuntan a la misma BD (`CommandCenterDb`) con schemas separados. `Products` (Inventory) se referencia desde `SaleItems` (Sales) vía `ProductId FK`.

---

## 7. Pipeline de Middleware

```
Request
  │
  ▼
┌─────────────────────────────────┐
│ GlobalExceptionHandlerMiddleware│  ← RFC 7807 ProblemDetails
├─────────────────────────────────┤
│ UseDefaultFiles + UseStaticFiles│  ← React build (wwwroot)
├─────────────────────────────────┤
│ UseCors                         │  ← LAN access
├─────────────────────────────────┤
│ VersionCheckMiddleware          │  ← X-Client-Version
├─────────────────────────────────┤
│ UseAuthentication (JWT)         │
├─────────────────────────────────┤
│ UseAuthorization (Roles)        │
├─────────────────────────────────┤
│ MapControllers                  │  ← 14 controladores
├─────────────────────────────────┤
│ MapHub<ExchangeRateHub>         │  ← SignalR
└─────────────────────────────────┘
```

### Manejo de Excepciones (RFC 7807)

| Excepción | HTTP | Error Code |
|---|---|---|
| `KeyNotFoundException` | 404 | NotFound |
| `ArgumentException` | 400 | BadRequest |
| `DbUpdateConcurrencyException` | 409 | ConcurrencyConflict |
| PostgreSQL `23505` | 409 | UniqueConstraintViolation |
| PostgreSQL `23502` | 400 | NotNullConstraintViolation |
| `Exception` | 500 | InternalServerError |

---

## 12. Seguridad

### Autenticación
- **JWT Bearer** con clave simétrica (`JWT_SETTINGS_KEY`)
- Tokens HMAC-SHA256, expiración configurable (ClockSkew = 0)
- MustChangePassword para usuarios semilla

### Autorización
- Roles: `Cashier`, `Manager`, `Admin`, `Driver`
- Control por endpoint (Authorize attributes)
- Headers `X-User-Id` y `X-User-Role` para trazabilidad

### Contraseñas
- `PasswordHasher` con hashing
- Contraseña semilla desde variable de entorno
- Login obliga cambio para admin semilla
