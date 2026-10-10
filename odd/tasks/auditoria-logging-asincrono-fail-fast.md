# Logging asíncrono no bloqueante + fail-fast bootstrap

Objetivo: tramo 8.158 (FINAL) del roadmap de auditoría — SRE-03 (erradicar el lock global y el I/O síncrono del camino de request en `AppLogger`/`ClientStateLogger` vía canal acotado + consumidor dedicado + `FlushAsync`) y CLEAN-03 (fail-fast con traza en el bootstrap de `secrets.json`; `LogWarn` sin throw en el `.env` opcional). Change: `openspec/changes/2026-10-10-audit-async-logging-fail-fast/` (ANEXO 8.158). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (SRE-03)**: productores no bloqueantes (`TryWrite` sobre canal acotado 4096, sin lock, sin `File.AppendAllText`) + consumidor único `LongRunning` por logger (rotación 10 MB/5, orden FIFO, contador de descartes con línea `[LOGGER]`) + `FlushAsync()` determinista (marcador FIFO + TCS, timeout 5 s) + drenaje `ProcessExit` (2 s) + contrato de rutas/formatos byte a byte intacto. Serilog NO adoptado (desviación documentada; G). Detalle: `specs/async-logging/spec.md`.
- **S2 (CLEAN-03)**: catch de `secrets.json` → `LogCrash(ex, "Backend.API.Program.SecretsBootstrap")` + `throw` (fail-fast; el archivo es obligatorio, precedencia máxima 8U-M2); catch del `.env` (Desarrollo, opcional) → `LogWarn` sin throw (desviación documentada del throw genérico); `Console.WriteLine` = 0 en `Program.cs`. Detalle: `specs/bootstrap-fail-fast/spec.md`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1 | delegated (writer) | SRE-03: 2 loggers asíncronos + flush + tests | pendiente |
| T2 | S2 | inline | CLEAN-03: 2 catches de Program.cs | pendiente |
| T3 | S1-S2 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.158 + cierre | pendiente |

## Log

- L1 (2026-10-10) — Pedido del usuario, verbatim: "arranco el 8.158 y terminamos el roadmap.".
- L2 (2026-10-10) — Verificación previa: **SRE-03 CONFIRMADO** (`AppLogger.WriteLog` L127-147 y `ClientStateLogger.WriteLog` L99-120 con `lock (_lock)` + `File.AppendAllText`; sin Serilog en ningún csproj; contrato de archivos externo consumido por installer/README/INSTALLATION y tests). **CLEAN-03 CONFIRMADO con matiz**: L48-51 (secrets.json, obligatorio `optional: false` si existe) y L126-130 (`.env` de Desarrollo, opcional) usan `Console.WriteLine`; la cola ya hace `LogCrash + throw` en el catch fatal externo; `LogError` no existe en AppLogger (se usa `LogCrash` con traza). Tests con lectura inmediata inventariados (AppLoggerTests ×2, SelfHealingResilienceTests ×1, GlobalExceptionHandler ×2, ErrorContractTests length, SecurityTests audit/start); los WPF pollean 3 s (permanecen).
- L3 (2026-10-10) — Próximo: T1 delegado a writer (superficies: Core/Logging/AppLogger.cs, Core/Logging/ClientStateLogger.cs, CommandCenter.Tests/** para flush y tests nuevos).
