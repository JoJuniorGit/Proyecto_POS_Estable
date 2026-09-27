# Architecture — Core (Visión General, Dependencias, Stack)

> Visión general del sistema, grafo de dependencias, stack tecnológico y estructura de directorios.

---

## 1. Visión General del Sistema

El sistema POS es una aplicación multiplataforma:

- **Backend API** (ASP.NET Core 10) — Servidor central, REST + SignalR
- **Web Frontend** (React 19 + Vite 8) — Cliente web para cajas y tablets
- **Desktop Client** (WPF .NET 10) — Cliente de escritorio
- **UpdaterService** — Auto-actualización del cliente WPF
- **Installer** (Inno Setup 7) — Paquete de despliegue

### Arquitectura de Alto Nivel

```
                    ┌─────────────────────────────────────────────┐
                    │       PostgreSQL 16/18 (CommandCenterDb)      │
                    │  ┌─────────────────┐  ┌──────────────────┐  │
                    │  │ InventoryDbContext│  │ SalesDbContext   │  │
                    │  └─────────────────┘  └──────────────────┘  │
                    └───────────────────┬─────────────────────────┘
                                        │ EF Core
                    ┌───────────────────┴─────────────────────────┐
                    │        Backend.API (ASP.NET Core 10)        │
                    │  ┌──────────────┐  ┌────────────────────┐  │
                    │  │   REST API   │  │  ExchangeRateHub   │  │
                    │  │(14 Controllers)│ │  (SignalR)        │  │
                    │  └──────────────┘  └────────────────────┘  │
                    │  ┌──────────────┐  ┌────────────────────┐  │
                    │  │ MediatR Bus  │  │ BackgroundService  │  │
                    │  └──────────────┘  │ Jobs               │  │
                    │                     └────────────────────┘  │
                    └───────────────────┬─────────────────────────┘
                                        │ HTTP + SignalR
              ┌─────────────────────────┼─────────────────────────┐
              ▼                         ▼                         ▼
    ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
    │  Web Frontend   │    │ Desktop.Client  │    │ UpdaterService  │
    │  React 19 + Vite│    │  WPF .NET 10    │    │  (auto-update)  │
    └─────────────────┘    └─────────────────┘    └─────────────────┘
```

---

## 6. Grafo de Dependencias entre Proyectos

```
                    ┌─────────────┐
                    │    Core     │  ← Raíz (sin dependencias)
                    └──────┬──────┘
                           │
              ┌────────────┼────────────┐
              │            │            │
              ▼            ▼            ▼
        ┌──────────┐ ┌──────────┐ ┌──────────┐
        │ Sales.   │ │Inventory.│ │Backend.  │
        │ Module   │ │ Module   │ │ API      │
        └────┬─────┘ └────┬─────┘ └────┬─────┘
             │            │       ┌────┴────┐
             ▼            ▼       ▼         ▼
        ┌─────────────────────────────┐ ┌──────────────┐
        │       Backend.API           │ │ Desktop.     │
        └─────────────────────────────┘ │ Client.Core  │
                                        └──────┬───────┘
                                               ▼
                                        ┌──────────────┐
                                        │ Desktop.     │
                                        │ Client       │
                                        └──────────────┘

        ┌──────────────┐
        │ Web.Frontend │  ← Independiente (HTTP)
        └──────────────┘
```

---

## 10. Puertos y Comunicación

| Puerto | Protocolo | Uso |
|---|---|---|
| 5000 | HTTP | Backend API principal (LAN) |
| 5001 | HTTPS | Backend API seguro (autofirmado) |
| 5173 | HTTP | Vite dev server (solo desarrollo) |

### Endpoints Principales

| Endpoint | Método | Descripción |
|---|---|---|
| `/health` | GET | Health check |
| `/api/auth/login` | POST | Login (cédula + password) |
| `/api/products` | GET/POST | Catálogo |
| `/api/sales` | GET/POST | Ventas |
| `/api/sales/{id}/claim` | POST | Reclamo exclusivo venta en espera |
| `/api/sales/{id}/release` | POST | Liberar bloqueo |
| `/api/cashdrawer/*` | GET/POST | Sesiones de caja |
| `/api/dailyclosure/*` | GET/POST | Cierres diarios |
| `/api/exchangerate` | GET | Tasa de cambio |
| `/api/users` | GET/POST | Gestión de usuarios |
| `/hubs/exchange-rate` | WebSocket | SignalR |

---

## 13. Pruebas

### CommandCenter.Tests

| Categoría | Tests | Descripción |
|---|---|---|
| Sales | Venta, Checkout, Pagos, Historial | Lógica de negocio |
| CashDrawer | Apertura, Cierre, Transacciones | Operaciones de caja |
| Inventory | Productos, Stock, Movimientos | Gestión de inventario |
| Resilience | Retry, Circuit Breaker, HealthPolling | Patrones de resiliencia |

### Suites

- .NET: `dotnet test` (677+ tests)
- Web: `npm test` (73+ tests) + `npm run lint` (oxlint 0 errores)

---

## 16. Tecnologías Utilizadas

| Capa | Tecnología | Versión |
|---|---|---|
| Backend API | ASP.NET Core | 10.x |
| ORM | EF Core | 10.x |
| Database | PostgreSQL | 16/18 |
| Mensajería | MediatR | latest |
| Tiempo real | SignalR | ^10.0.5 |
| JWT | Microsoft.IdentityModel.Tokens | built-in |
| Web Frontend | React | 19.2.7 |
| Bundler | Vite | 8.1.1 |
| Linting | oxlint | 1.71.0 |
| Desktop UI | WPF + MaterialDesignInXAML | 5.3.0 |
| Desktop MVVM | CommunityToolkit.Mvvm | latest |
| Installer | Inno Setup | 7.x |
| Testing | xUnit + Moq + coverlet / node:test | latest |

---

## 17. Estructura de Directorios

```
Proyecto_POS_Estable/
├── Backend.API/           # API REST + SignalR
├── Core/                  # Librería compartida (entidades, DTOs, interfaces)
├── Sales.Module/          # Dominio de ventas
├── Inventory.Module/      # Dominio de inventario
├── Logistics.Module/      # Experimental
├── Desktop.Client/        # WPF Client (Views, Converters, Themes)
├── Desktop.Client.Core/   # MVVM + Resilience (ViewModels, Services)
├── UpdaterService/        # Auto-update
├── Web.Frontend/          # React Client (pages, components, context)
├── CommandCenter.Tests/   # Tests unitarios
├── installer/             # Inno Setup + scripts
├── scripts/               # Build & deploy
├── docs/                  # Documentación
├── CommandCenter.slnx     # Solution
└── ARCHITECTURE.md        # Este documento
```

---

## 18. Resumen de Dependencias Críticas

| Componente | Depende de | Riesgo si falla |
|---|---|---|
| `Core` | Ninguno | Todo el sistema colapsa |
| `Sales.Module` | Core | Ventas y caja no funcionan |
| `Inventory.Module` | Core | Inventario no se actualiza |
| `Backend.API` | Core, Sales, Inventory | API completa cae |
| `Desktop.Client` | Core, Backend API | WPF inoperable |
| `Web.Frontend` | Backend API | Web inoperable |
| `PostgreSQL` | Ninguno (infra) | Datos inaccesibles |
