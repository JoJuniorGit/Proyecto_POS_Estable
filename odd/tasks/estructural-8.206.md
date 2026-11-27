# Rate limit del hub y vertical de clientes (8.206)

Objetivo: cerrar el residual de abuso del hub de autorizaciones y reducir otra vertical de `SalesService` sin cambiar contratos públicos.
Rama: `V0.15`. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + carril PostgreSQL aislado.

## Specs

- **S1 — Rate limit del hub.** `AuthorizationHub` limita `RequestAuthorization` y `ResolveAuthorization` por usuario y operación con ventana fija configurable (`RateLimiting:AuthorizationHubPermitLimit`, default 10; `RateLimiting:AuthorizationHubWindowSeconds`, default 60). Al exceder, devuelve un resultado fallido con mensaje y segundos de reintento; no invoca al coordinador.
- **S2 — Límite aislado y testeable.** `AuthorizationHubRateLimiter` es singleton, usa `TimeProvider` para tests deterministas, separa usuarios/operaciones y limpia ventanas vencidas de forma oportunista.
- **S3 — Catálogo de clientes fuera de `SalesService`.** `SalesCustomerService` encapsula `GetDefaultCustomerAsync`, `GetCustomersAsync`, `CreateCustomerAsync`, `UpdateCustomerAsync` y `DeleteCustomerAsync` con su caché. `SalesService` delega con las mismas firmas; `UpdateSaleCustomerAsync` permanece en `SalesService` por ser mutación de venta.
- **S4 — Verificación.** Build Release 0/0; tests del limitador y del hub verdes; focalizados de clientes verdes; suite completa con PostgreSQL verde.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S1, S2 | completado | limitador + integración en hub; 4/4 tests nuevos + 26/26 hub |
| WU2 | S3 | completado | `SalesCustomerService`; focalizados clientes 100/100 (sin PG) |
| WU3 | S4 | completado | build 0/0; suite completa 3597/3597 con PostgreSQL |

Ruta: directa inline. El limitador es un servicio singleton nuevo; la extracción de clientes es interna y conserva la fachada.

## Log

- **L1 (2026-10-26):** inicio de 8.206 por pedido del mantenedor de avanzar con ambos residuales. Estado limpio en `a403e03`.
- **L2 (2026-10-26, cierre):** rate limit por usuario/operación en el hub (config `RateLimiting:AuthorizationHubPermitLimit=10`, ventana 60 s) y `SalesCustomerService` con la vertical de catálogo. Build Release 0/0. Tests nuevos del limitador y del hub 4/4; hub existente 26/26; focalizados de clientes 100/100 (sin PG). Suite completa con PostgreSQL aislado: **3597/3597**, 0 fallos, 0 omitidos. El clúster se detuvo al finalizar.
