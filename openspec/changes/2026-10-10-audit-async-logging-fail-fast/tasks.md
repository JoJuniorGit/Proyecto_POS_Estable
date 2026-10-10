# Tasks: Logging asíncrono + fail-fast bootstrap (8.158)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~250-450 |
| Budget risk | Medium |
| Chained PRs | Continúa la cadena V0.15 (slices por unidad) |
| Split | 3 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. SRE-03 (T1) | `--filter "FullyQualifiedName~AppLogger\|~SelfHealingResilience\|~GlobalExceptionHandler\|~ErrorContract\|~SecurityTests\|~ViewModelCatchLogging\|~CartCommitResilience\|~AsyncLogging"` | Revertir 2 loggers + tests |
| 2. CLEAN-03 (T2) | build + grep `Console.WriteLine`=0 | Revertir Program.cs (2 catches) |
| 3. Verificación + cierre (T3) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1) — SRE-03: logging asíncrono no bloqueante

- [ ] 1.1 `AppLogger` + `ClientStateLogger`: canal acotado + consumidor dedicado + `FlushAsync` + drenaje ProcessExit; API y formatos intactos.
- [ ] 1.2 Tests: flush en sitios de lectura inmediata (grep exhaustivo) + nuevos tests de flush/orden/LogDbError; RED compile-level → GREEN.
- [ ] 1.3 Build 0/0 + focused verde; commit `perf(8.158)`.

## T2 (S2) — CLEAN-03: bootstrap fail-fast

- [ ] 2.1 secrets.json → `LogCrash` + `throw`; `.env` → `LogWarn` sin throw; `Console.WriteLine` = 0 en Program.cs.
- [ ] 2.2 Build 0/0 + focused; commit `fix(8.158)`.

## T3 (S1-S2) — Verificación + cierre

- [ ] 3.1 Verificador independiente (S1/S2): contrato de formatos, flush, no-bloqueo, fail-fast.
- [ ] 3.2 Suite CON Postgres + cobertura.
- [ ] 3.3 `verify-report.md` + ANEXO 8.158 + tracker/state + commit `docs(8.158)`.

## Notes

- Tracker: `odd/tasks/auditoria-logging-asincrono-fail-fast.md`.
- Los writers NO commitean; el padre commitea por unidad.
- 8.158 cierra el roadmap completo de la auditoría re-emitida.
