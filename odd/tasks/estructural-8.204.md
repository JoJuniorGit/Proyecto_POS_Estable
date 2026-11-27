# Refactor estructural y logging (8.204)

Objetivo: reducir el God object de caja, migrar un servicio crítico a logging estructurado y subir la política de hashing, sin cambiar contratos públicos.
Rama: `V0.15`. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + carril PostgreSQL aislado.

## Specs

- **S1 — Saldos fuera de `CashDrawerService`.** `CashDrawerBalanceService` encapsula `GetCurrentBalanceLocalAsync`, `GetPhysicalBalanceAsync` y el mapa de moneda por método; el servicio público delega sin cambiar firmas ni semántica.
- **S2 — Idempotencia fuera de `CashDrawerService`.** `CashDrawerIdempotencyStore` encapsula replay, persistencia, existencia y detección de violación única; `AddTransactionAsync` conserva la atomicidad y el comportamiento de replay.
- **S3 — `AuthService` con logging estructurado.** Se inyecta `ILogger<AuthService>` opcional; los eventos operativos usan templates con `{Username}`/`{Attempt}`/`{LockoutEnd}`. Los eventos de auditoría usan `AppLogger.LogStructuredSecurity`.
- **S4 — Hashing actualizado.** PBKDF2-SHA256 sube a 600.000 iteraciones; `NeedsRehash` detecta hashes legados y `AuthenticateAsync` rehashea en el login exitoso. Los hashes viejos siguen verificando.
- **S5 — Verificación.** Build 0/0; tests focales verdes; suite completa con PostgreSQL verde.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU6 | S1 | completado | `CashDrawerBalanceService`; focalizados 141/141 |
| WU7 | S2 | completado | `CashDrawerIdempotencyStore`; focalizados 141/141 |
| WU8 | S3, S4 | completado | `AuthService` + PBKDF2 600k + `NeedsRehash`; suite |
| WU9 | S5 | completado | build 0/0; suite completa 3593/3593 con PostgreSQL |

Ruta: directa inline. La extracción es interna (clases `internal`) y no altera DI ni contratos.

## Log

- **L1 (2026-10-26):** extracción de saldos e idempotencia de caja, migración de `AuthService` a `ILogger` y subida de PBKDF2. Se conserva un solo escritor secuencial. Residuales: split de `SalesService`, extracción del anclaje de tasa del controlador, matemática de preview en UI, rate limit del hub, health/pairing y credenciales locales de desarrollo.
- **L2 (2026-10-26, cierre):** build 0/0; suite completa **3593/3593** con el carril PostgreSQL. Un test de `PosSearchSuggestionsRoundingTests` falló una vez por timing bajo paralelismo, pasó aislado y volvió a pasar en la segunda corrida completa; no toca los archivos modificados.
