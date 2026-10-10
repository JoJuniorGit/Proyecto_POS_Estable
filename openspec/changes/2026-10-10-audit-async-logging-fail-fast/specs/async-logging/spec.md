# Spec (delta): Logging asíncrono no bloqueante — SRE-03 (8.158)

Fuente: auditoría re-emitida, SRE-03: "Inanición del ThreadPool por AppLogger síncrono con lock global" (Core/Logging/AppLogger.cs L127-147; Core/Logging/ClientStateLogger.cs L100-120). Remediación exigida textual: "Reemplazar el logger casero por el pipeline estándar de .NET con Serilog y canal asíncrono no bloqueante en memoria (`Serilog.Sinks.Async` con `Channel<LogEvent>` acotado)".

Desviación documentada (D1, ver proposal): se implementa la ESENCIA SRE — canal asíncrono acotado no bloqueante, sin lock en productores, sin I/O síncrono en el camino de request — preservando la fachada estática y el contrato de archivos externo. Serilog NO se adopta en este tramo (dependencias nuevas + cambio de artefactos consumidos por installer/docs/tests); queda como recomendación G.

## ADDED Requirements

### Requirement: REQ-AL-01 — Productores no bloqueantes

`AppLogger` y `ClientStateLogger` MUST mantener su API pública intacta (métodos estáticos y propiedades de rutas) y MUST encolar cada entrada en un canal acotado (capacidad 4096, `BoundedChannelFullMode.Wait`, `SingleReader = true`) mediante `TryWrite` — nunca `lock`, nunca `File.AppendAllText` en el productor. Si el canal está lleno, la entrada MUST descartarse incrementando un contador atómico de descartes; el productor MUST NOT bloquear jamás.

#### Scenario: Llamada bajo saturación

- GIVEN el canal lleno
- WHEN se llama `LogWarn`/`LogInfo`/etc.
- THEN retorna sin bloquear y el descarte queda contabilizado.

### Requirement: REQ-AL-02 — Consumidor único y resiliente

Cada logger MUST tener UN consumidor dedicado (`TaskCreationOptions.LongRunning`) que escribe a disco con la rotación existente (10 MB / 5 archivos). El consumidor MUST: preservar el orden FIFO por archivo; capturar toda excepción por entrada (el logger jamás lanza); y, si el contador de descartes es > 0, registrar una línea `[LOGGER]` de descartes en el siguiente volcado y resetearlo. `AppLogger.LogDbError` MUST seguir escribiendo en `db-errors.log` Y `crash.log` (dos entradas).

#### Scenario: Excepción de disco

- GIVEN un fallo de escritura (permiso/disco)
- THEN la entrada se descarta silenciosamente y el consumidor continúa procesando.

### Requirement: REQ-AL-03 — Flush determinista y drenaje en salida

Ambos loggers MUST exponer `FlushAsync()` que encola un marcador FIFO y completa cuando TODAS las entradas previas fueron escritas (timeout de seguridad 5 s). El constructor estático MUST registrar un drenaje best-effort en `AppDomain.ProcessExit` (`FlushAsync().Wait(2000)`), para no perder `crash.log` en muertes gestionadas.

#### Scenario: Escritura → flush → lectura

- GIVEN entradas encoladas
- WHEN se aguarda `FlushAsync()`
- THEN el contenido está en disco y en orden.

### Requirement: REQ-AL-04 — Contrato de archivos preservado byte a byte

Rutas (`start.log`, `crash.log`, `db-errors.log`, `security-audit.log`, `warn.log`, `client-resilience.log`), formatos de línea (`[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] msg\n----------------------------------------\n` con hora local en `AppLogger`; `[yyyy-MM-ddTHH:mm:ss.fffZ] [LEVEL] [origin] msg\n` UTC en `ClientStateLogger`) y rotación MUST ser EXACTAMENTE los actuales. Consumidores externos (installer, docs, tests) no se alteran.

#### Scenario: Formato existente

- GIVEN los tests de formato existentes
- WHEN corren con flush
- THEN pasan sin cambios de aserciones de formato.

### Requirement: REQ-AL-05 — Tests alineados a la asincronía

Todo test que lea contenido/existencia de log inmediatamente tras una operación MUST aguardar el `FlushAsync()` correspondiente antes de asertar (sitios conocidos: `AppLoggerTests` ×2, `SelfHealingResilienceTests` ×1, `GlobalExceptionHandlerMiddlewareTests` ×2, `ErrorContractTests` (length), `SecurityTests` (audit/start); el writer MUST grepear exhaustivamente `ReadAllText|ReadToEnd|File.Exists|Length` sobre rutas de log). Los helpers con polling (3 s) pueden permanecer. Nuevos tests MUST cubrir: flush tras escrituras (contenido y orden), `LogDbError` a ambos archivos, formato preservado.

#### Scenario: RED compile-level

- GIVEN los tests nuevos que llaman `FlushAsync()`
- THEN la compilación falla nombrando el miembro ausente (RED divulgado del refactor).
