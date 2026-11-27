# Remediación de auditoría DevSecOps/Performance (8.203)

Objetivo: cerrar los hallazgos ALTO/MEDIO contenidos de la auditoría integral sin romper contratos de clientes.
Rama: `V0.15`. Entrega: work units con commit propio, build 0/0 y suite focalizada + carril PostgreSQL.
Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` y `pwsh scripts/test-postgres-lane.ps1`.

## Specs

- **S1 — Logging sin I/O por línea ni catches vacíos.** `AsyncLogSink` mantiene un `StreamWriter` bufferizado por archivo, rota por tamaño, expone contadores de descartes/fallos y nunca usa `catch { }`; `FlushAsync` fuerza el flush.
- **S2 — Tasa de factura proveedor anclada.** La confirmación con `AppliedRate` explícito se valida contra la tasa oficial del día; una desviación mayor al 10% se rechaza antes de abrir la transacción. La moneda sigue siendo canónica.
- **S3 — Vuelto bajo re-check de sesión.** `RecordSaleChangeAsync` revalida `Open` bajo el lock de sesión, no solo la transacción ambiente.
- **S4 — Cierre diario serializado.** Los cierres concurrentes se serializan con un advisory lock transaccional de clase 5, cubriendo el rollover de sesión.
- **S5 — Hubs con cancelación.** `AuthorizationHub` propaga `Context.ConnectionAborted` a las operaciones de dominio.
- **S6 — JWT exige HTTPS fuera de Development.** `RequireHttpsMetadata` default `true` en producción; no se debilita el arranque de desarrollo/tests.
- **S7 — Bulk prices idempotente.** `apply` exige `Idempotency-Key`, persiste la respuesta en la misma transacción del `InventoryDbContext` (SQL crudo en PostgreSQL) y replica ante replay; una colisión concurrente revierte el apply completo.
- **S8 — Verificación.** Build 0/0; tests focales verdes; lane PostgreSQL verde; sin drift EF.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S1 | completado | `AsyncLogSink` bufferizado; `AsyncLoggingTests` + `ConcurrencyPhase4Tests` 10/10 |
| WU2 | S2, S6 | completado | test de anclaje de tasa; build 0/0 |
| WU3 | S3, S4, S5 | completado | suite focalizada; carril PostgreSQL |
| WU4 | S7 | completado | `BulkPriceConcurrencyTests` 2/2 en PostgreSQL; suite completa |
| WU5 | S8 | completado | build 0/0; suite 3592/3592 con carril PostgreSQL |

Ruta: directa inline. Los cinco work units comparten contratos y se verifican secuencialmente; el padre ya mapeó los archivos exactos.

## Log

- **L1 (2026-10-26):** remediación derivada de la auditoría integral de 4 fases. Se priorizan ALTO/MEDIO contenidos; los refactors estructurales (God objects, migración completa a `ILogger`, matemática de preview en UI) quedan como backlog separado por riesgo de contrato.
- **L2 (2026-10-26, cierre):** WU1-WU4 implementados. Evidencia: `dotnet build CommandCenter.slnx -c Release` 0/0; suite completa con carril PostgreSQL **3592/3592**; `BulkPriceConcurrencyTests` 2/2 (incluye el nuevo test de idempotencia relativa); `AsyncLoggingTests` + `ConcurrencyPhase4Tests` 10/10. La ventana de idempotencia de bulk prices es de 60 s y deduplica el mismo payload+actor; no requiere cambios en Web/WPF. La validación de tasa de factura proveedor falla cerrada si el proveedor oficial no responde. Residuales estructurales y de UI permanecen como backlog (God objects, `AppLogger` → `ILogger`, matemática de preview en WPF/React, H-02 desktop LAN HTTP).
