# Hardening de caja e idempotencia (8.202)

Objetivo: cerrar los riesgos P1/P2 de la auditoría no funcional con 4 commits H1-H4, suite completa en verde y evidencia en un ANEXO nuevo.
Rama: `V0.15` (no es la rama por defecto). Entrega: 4 commits `feat/fix/test/refactor(8.202)`.
Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + `pwsh scripts/test-postgres-lane.ps1`.

## Specs

- **S1 — Re-check bajo lock en `CashDrawerService.AddTransactionAsync`.** El advisory lock de la sesión se adquiere antes de validar el estado y la sesión se relee dentro de la región bloqueada; si fue cerrada mientras se esperaba, la escritura aborta con `InvalidOperationException` ("No se pueden registrar movimientos en una sesión de caja cerrada."). Fuera de Npgsql no se usa advisory lock y se conserva la validación previa.
- **S2 — Participación del checkout y demás escritores en el lock de sesión.** Todo flujo que agrega `CashTransaction` a la gaveta adquiere `pg_advisory_xact_lock(sessionId)` y revalida `Open` antes de persistir, respetando el orden `Sale/Customer -> CashDrawerSession`. Cubre checkout, abonos de venta en espera (individual y lote), abono inicial de HoldSale, cobros CxC y devoluciones CxC. La ventana del lock se mantiene corta (no incluye lecturas de catálogo ni deducción de stock).
- **S3 — Agregación SQL de saldos.** `GetCurrentBalanceLocalAsync` y `GetPhysicalBalanceAsync` agregan en PostgreSQL por `(PaymentMethodId, Type)` con `Sum`; no materializan todas las filas de la sesión. La atribución de moneda por método y el fallback a Bs.S se conservan.
- **S4 — Idempotencia atómica de movimientos manuales y adelantos.** El `Idempotency-Key` viaja al servicio/coordinador y el `IdempotentRequest` se persiste en la misma transacción que el movimiento/venta, bajo el lock de sesión. Un duplicado concurrente devuelve la misma respuesta sin duplicar efectos; si el insert compite, la transacción completa se revierte.
- **S5 — `verifySucceeded` real.** Las estrategias de ejecución de la caja y del adelanto verifican la clave de idempotencia persistida ante un commit ambiguo; si el commit sí ocurrió, reconstruyen la respuesta almacenada en lugar de reintentar el efecto.
- **S6 — Pruebas de concurrencia PostgreSQL.** Nuevos tests gated `~Postgres` con bases aisladas certifican: (a) carrera cierre vs escritura/checkout sin `CashTransaction` posterior al cierre y con inclusión correcta si la escritura gana; (b) misma clave en paralelo produce exactamente una fila y la misma respuesta.
- **S7 — CxC filtrada en SQL y paginación opcional.** Los predicados de cliente, estado, búsqueda y mora (`DueDateUtc` + gracia) se ejecutan en `IQueryable`; los totales se agregan en SQL. La paginación es opcional y aditiva (`Page`/`PageSize`), sin romper WPF/Web que hoy no la envían.
- **S8 — Índices de ventana Z.** Migración idempotente (`CREATE INDEX IF NOT EXISTS`) para `SalePayments(CreatedAt)`, `ReceivablePayments(ReceivedAtUtc)` y `PettyCashExpenses(CreatedAtUtc)`, con snapshot y modelo sincronizados (drift 0).
- **S9 — OCR sin bloqueo de thread-pool.** `TesseractOcrEngine` espera el gate con `await WaitAsync(ct)` y ejecuta el trabajo CPU-bound en `Task.Run` awaited; no usa `SemaphoreSlim.Wait` dentro del flujo asíncrono.
- **S10 — SecurityStamp con cancelación.** `ValidateStampAsync` acepta `CancellationToken`, el middleware propaga `HttpContext.RequestAborted` y la consulta EF lo usa. Se documenta explícitamente que las mutaciones no tienen ventana de gracia (verificación inmediata) y que las lecturas usan la caché de 45 s.
- **S11 — Verificación global.** `dotnet build CommandCenter.slnx -c Release` 0 errores/0 advertencias; suite unitaria/integración 100% verde; carril PostgreSQL real verde; `npm test`/lint si no se toca Web (no se toca).
- **S12 — ANEXO.** Nuevo `ANEXO POST-CIERRE (XXXVI)` en `docs/reporte.txt` con cambios, evidencia por comando y residuales.

## Tasks

| ID | Specs | Ruta | Estado | Evidencia |
| --- | --- | --- | --- | --- |
| H1 | S1, S2, S3 | directa | completado | `9d8cf88`; build 0/0; 413/413 focalizados sin carril |
| H2 | S4, S5 | directa | completado | `5dc1743`; build 0/0; 390/390 focalizados sin carril |
| H3 | S6 | directa | completado | 2 archivos nuevos, 4 tests; carril PostgreSQL 39/39 |
| H4 | S7, S8, S9, S10, S12 | directa | pendiente | — |

Ruta directa justificada: el padre ya mapeó los archivos y líneas exactos en la auditoría; los cuatro work units son un único refactor entrelazado (contratos de servicio, locks y tests) donde un writer por tarea duplicaría contexto y riesgo de drift. Se mantiene un solo writer secuencial.

## Log

- **L1 (pedido original, verbatim):** ver bloque siguiente.
- **L2 (2026-10-26):** se elige reordenar el bloque de vuelto del checkout para que el lock de sesión cubra solo la persistencia final y no la deducción de stock. Se conserva el orden canónico `Sale/Customer -> Session`.
- **L3 (2026-10-26):** la paginación de CxC queda opcional para no truncar la cartera que consumen WPF/Web; el objetivo P2 se cumple moviendo filtros y totales a SQL.
- **L4 (2026-10-26, H3):** la carrera cierre-vs-checkout admite dos desenlaces válidos: (a) el checkout ya resolvió la sesión y el cierre gana → falla con "sesión de caja cerrada"; (b) el cierre commitea antes de `GetOrCreateActiveSessionAsync` → el alta automática abre una sesión posterior y el cobro se asienta allí. La invariante certificada es que **nunca** se escribe en la sesión cerrada; ambos desenlaces se prueban en el mismo test. Evidencia: 4/4 tests nuevos y 39/39 del carril PostgreSQL.

### L1 — Pedido original

> Actúa como un Principal .NET Performance & Reliability Engineer. Excelente trabajo con la auditoría Read-Only. Vamos a ejecutar el Plan de Endurecimiento (Hardening H1 a H4) para cerrar definitivamente los riesgos P1 y P2 antes de abrir nuevas funcionalidades.
>
> Ejecuta el trabajo en 4 commits estructurados (H1 a H4) siguiendo estas reglas exactas:
>
> ### H1 — Serialización Estricta de Sesión de Caja y Agregación SQL de Saldos
> 1. **Re-check bajo Lock en `CashDrawerService.AddTransactionAsync`:** Adquiere `pg_advisory_xact_lock(sessionId)` ANTES de dar por válido el estado de la sesión y re-verifica explícitamente dentro de la región bloqueada que la sesión sigue en estado `Open`. Si fue cerrada mientras esperaba el lock, aborta lanzando la excepción de dominio correspondiente.
> 2. **Participación de `SalesService.Checkout` en el Lock de Sesión:** En todo flujo de checkout que genere movimientos de efectivo en gaveta, adquiere el advisory lock de la sesión activa respetando siempre el orden jerárquico canónico de bloqueos (`Sale / Customer -> CashDrawerSession` para impedir deadlocks) y re-verifica que la sesión permanezca `Open` antes de registrar los `CashTransaction`.
> 3. **Saldo Físico Server-Side (`GetCurrentBalanceLocalAsync` / `GetPhysicalBalanceAsync`):** Elimina la materialización en memoria de todos los movimientos de la sesión mientras se retiene el lock. Calcula los saldos en `Bs.S` y `USD` mediante agregación directa en SQL (`SumAsync` / proyección agrupada en PostgreSQL) para reducir el tiempo de retención del lock a sub-milisegundos.
>
> ### H2 — Idempotencia Atómica y `verifySucceeded` en Operaciones de Caja
> 1. **Movimientos Manuales (`CashDrawerService`) y Avances de Efectivo (`CashAdvanceCoordinator`):** Alinea ambos flujos con el patrón transaccional que ya usamos en `Checkout` y `CreditPayments`: pasa el `Idempotency-Key` hacia adentro del servicio/coordinador y persiste el registro de idempotencia dentro de la misma transacción de base de datos que crea el movimiento de caja / venta (bajo el advisory lock correspondiente).
> 2. **Verificación de Commits Ambiguos (`verifySucceeded`):** Reemplaza `verifySucceeded: null` en las estrategias de ejecución transaccional de caja por un delegado real que consulte si la llave de idempotencia o el movimiento ya quedó persistido en PostgreSQL ante un corte de conexión durante el commit.
>
> ### H3 — Suite de Pruebas de Concurrencia Real sobre PostgreSQL
> Agrega tests de integración concurrentes contra PostgreSQL que certifiquen:
> 1. **Carrera Cierre vs. Escritura (`Session Closure Race`):** Lanzar en paralelo `CloseSessionAsync` contra `AddTransactionAsync` y `CompleteSaleAsync`. Verificar que jamás quede un `CashTransaction` asociado a una sesión cerrada (si el cierre gana el lock, la escritura posterior debe fallar limpiamente por sesión cerrada; si la escritura gana el lock primero, su monto debe quedar incluido en los totales del cierre).
> 2. **Carrera de Idempotencia (`Same-Key Parallel Race`):** Lanzar 2 o más peticiones concurrentes con el mismo `Idempotency-Key` en movimientos manuales de caja y en avances de efectivo, certificando que se persista exactamente 1 movimiento en base de datos y las peticiones concurrentes reciban la misma respuesta sin duplicar efectos financieros.
>
> ### H4 — Escalabilidad de Consultas (CxC + Índices de Cierre Z) e Higiene Async
> 1. **Filtrado SQL y Paginación/Límite en `CreditService.GetCreditReceivablesAsync`:** Mueve los predicados de búsqueda, estado y vencimiento (`DueDateUtc`) al `IQueryable` en PostgreSQL (`AsNoTracking`) antes de materializar en memoria, manteniendo compatibilidad con los contratos actuales de WPF y Web.
> 2. **Índices para `DailyClosureService`:** Añade mediante migración idempotente (`CREATE INDEX IF NOT EXISTS`) los índices faltantes por fecha para las consultas de ventana del Cierre Z en `SalePayments(CreatedAt)`, `ReceivablePayments(ReceivedAtUtc)` y `PettyCashExpenses(CreatedAtUtc)` (actualizando snapshot y tests de drift).
> 3. **Higiene Async en OCR y SecurityStamp:** En `TesseractOcrEngine`, reemplaza el bloqueo síncrono `SemaphoreSlim.Wait` dentro de `Task.Run` por espera asíncrona no bloqueante (`await WaitAsync(cancellationToken)`). En `SecurityStampValidationMiddleware` / `SecurityStampValidator`, propaga `HttpContext.RequestAborted` (`CancellationToken`) a la consulta de base de datos y respeta la ventana de caché estricta en lecturas o documenta el comportamiento sin dejar queries huérfanas.
>
> Arranca con H1 y avanza secuencialmente hasta H4, verificando que toda la suite unitaria, de integración y de concurrencia quede 100% en verde, y registra la evidencia en el nuevo ANEXO del reporte.
