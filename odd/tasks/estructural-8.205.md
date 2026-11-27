# Split de SalesService y anclaje de tasa (8.205)

Objetivo: reducir la fachada de `SalesService` extrayendo su vertical de consultas y unificar la política de anclaje de tasa BCV duplicada con `CashDrawerController`, sin cambiar contratos públicos.
Rama: `V0.15`. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + carril PostgreSQL aislado.

## Specs

- **S1 — Historial fuera de `SalesService`.** `SalesHistoryService` encapsula `GetSalesHistoryAsync`, `GetSaleHistoryDetailAsync`, `GetPendingPickupsAsync`, `CountPendingPickupsAsync`, el mapeo `MapToHistoryDetail` y `ToLikePattern`. `SalesService` delega con las mismas firmas; `ConfirmPickupAsync` permanece en `SalesService` porque es una mutación.
- **S2 — Anclaje de tasa único.** `ExchangeRateAnchorService` centraliza validación, redondeo, lectura BCV, tolerancia y decisión (aceptar/anclar/rechazar/fail-open). `SalesService.ResolveAnchoredRateAsync` y `CashDrawerController.ResolveAnchoredRateAsync` delegan en la misma política y conservan el mensaje público de rechazo.
- **S3 — Semántica conservada.** Sin BCV o error de BCV: fail-open con la tasa recibida y log. Desvío > tolerancia: ancla a BCV. Desvío ≥ 100%: `ArgumentException` con el mensaje existente. Tolerancia inválida o ilegible: 10% por defecto.
- **S4 — Verificación.** Build Release 0/0; focalizados de historial, pickups, checkout y caja verdes; suite completa con PostgreSQL verde.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S1 | completado | `SalesHistoryService`; focalizados historial/pickups 35/35 (sin PG) |
| WU2 | S2, S3 | completado | `ExchangeRateAnchorService`; focalizados rate/anchor 48/48 + caja 88/88 |
| WU3 | S4 | completado | build Release 0/0; suite completa 3593/3593 con PostgreSQL |

Ruta: directa inline. Las extracciones son internas o de política pura y no alteran DI ni contratos públicos.

## Log

- **L1 (2026-10-26):** extracción de la vertical de consultas de historial/entregas de `SalesService` y de la política de anclaje de tasa BCV compartida con `CashDrawerController`. `ConfirmPickupAsync` queda en `SalesService` por ser mutación. Se conservan mensajes de rechazo, fail-open y tolerancia.
- **L2 (2026-10-26, cierre):** build `CommandCenter.slnx -c Release` 0 errores / 0 advertencias. Focalizados: historial/pickups 35/35 (el test PostgreSQL queda para el carril), rate/anchor 48/48, caja 88/88. Suite completa con el clúster PostgreSQL aislado en el puerto 55432: **3593/3593**, 0 fallos, 0 omitidos; el clúster se detuvo al finalizar. `SalesServicePostgresHistoryTests` pasó dentro de la suite.
