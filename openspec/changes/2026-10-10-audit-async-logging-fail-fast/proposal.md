# Proposal: Logging asíncrono no bloqueante (SRE-03) + Fail-Fast en bootstrap (CLEAN-03) — ANEXO 8.158

## Contexto

Tramo FINAL del roadmap de la auditoría re-emitida ("arranco el 8.158 y terminamos el roadmap"). Alcance: **SRE-03** (inanición del ThreadPool por `AppLogger` síncrono con lock global) y **CLEAN-03** (swallowing de excepciones en el bootstrap de `Program.cs`).

Verificación previa (estática, de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| SRE-03 | CONFIRMADO | `AppLogger.WriteLog` (Core/Logging/AppLogger.cs L127-147) y `ClientStateLogger.WriteLog` (L99-120): `lock (_lock)` + `File.AppendAllText` (I/O síncrono bloqueante) en el camino de request. Sin Serilog en ningún csproj. Contrato de archivos EXTERNO: installer (`Configure-PosService.ps1`, `installer/README.md`) y `docs/INSTALLATION*.md` referencian `start.log`/`crash.log`/`db-errors.log`; tests leen los 6 archivos. |
| CLEAN-03 | CONFIRMADO (con matiz) | L48-51: catch de carga de `secrets.json` (archivo OBLIGATORIO con precedencia máxima 8U-M2, `optional: false` cuando existe) → `Console.WriteLine` sin traza. L126-130: catch del recorrido de `.env` en Desarrollo (camino OPCIONAL: appsettings ya cargó) → `Console.WriteLine`. La cola del archivo ya hace `LogCrash + throw` en el catch fatal externo (L165-169); no hay más `Console.WriteLine`. |

## Alcance

1. **S1 (SRE-03)**: `AppLogger` y `ClientStateLogger` pasan a productores no bloqueantes sobre `BoundedChannel<T>` (capacidad 4096, `FullMode.Wait`) con UN consumidor dedicado por logger (`LongRunning`), rotación existente en el consumidor, contador atómico de descartes, `FlushAsync()` determinista (marcador FIFO + TCS, timeout 5 s) y drenaje best-effort en `AppDomain.ProcessExit`. API pública y contrato de archivos (rutas, formatos, rotación 10 MB/5) INTACTOS byte a byte.
2. **S2 (CLEAN-03)**: catch de `secrets.json` → `AppLogger.LogCrash(ex, contexto)` + `throw;` (fail-fast con traza); catch del `.env` → `AppLogger.LogWarn(...)` sin throw; `Console.WriteLine` = 0 en `Program.cs`.

Fuera de alcance: Serilog (ver D1), cualquier refactor no exigido.

## Enfoque y decisiones

- **D1 (SRE-03)**: se implementa la ESENCIA SRE del hallazgo — canal acotado asíncrono, cero lock en productores, cero I/O síncrono en el camino de request — detrás de la fachada estática, preservando el contrato de archivos que consumen installer, docs y tests. NO se adopta Serilog en este tramo: 3 dependencias nuevas en un backend desplegado por installer/updater, + cambio de artefactos externos (`logs/pos-.log` vs `crash.log`/`start.log` referenciados por el installer y los docs de instalación) = riesgo de entrega mayor que el beneficio; la parte de "ingesta estructurada" queda como recomendación G con su propio assessment.
- **D2 (CLEAN-03)**: `AppLogger` no expone `LogError`; se usa `LogCrash(ex, contexto)` que vuelca la traza completa (exactamente lo exigido por el diagnóstico del audit). El `throw` se aplica SOLO al catch de `secrets.json` (configuración obligatoria); el catch del `.env` registra `LogWarn` SIN throw — desviación documentada del "throw" genérico del audit: el `.env` es un camino opcional de Desarrollo y lanzar rompería el arranque dev sin beneficio.
- **D3 (tests)**: todo test con lectura inmediata de logs aguarda `FlushAsync()` antes de asertar; los helpers con polling de 3 s (`ViewModelCatchLoggingTests`, `CartCommitResilienceTests`) permanecen como están (ya toleran asincronía).

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~250-450 líneas. Slices: SRE-03 / CLEAN-03 / cierre.
- Riesgo principal: contrato de formatos de log (mitigado: formatos byte-idénticos + tests de formato existentes que deben pasar sin cambios de aserciones); concurrencia (mitigado: canal FIFO con consumidor único por logger + flush determinista).
- RDD: off (clone-local) → verificación independiente + suite/cobertura.

## Cierre del roadmap

Con 8.158 verificado, la matriz completa de la auditoría re-emitida queda cerrada: 5 remediados en 8.149 + tramos 8.152-8.159 + 3 refutaciones documentadas con evidencia (SRE-04, SRE-05, PERF-03) + desviaciones/parciales registradas (PERF-02 por spec canónico; SEC-01 residual; Serilog como G).
