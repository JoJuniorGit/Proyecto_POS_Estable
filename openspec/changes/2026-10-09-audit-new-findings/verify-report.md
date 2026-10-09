# Verify Report: Hallazgos nuevos de la auditoría re-emitida (8.152)

## Veredicto

**PASS WITH WARNINGS** — T1 (SEC-08), T2 (CLEAN-06) y S3 (SRE-04 refutado) cumplen sus specs con evidencia reproducible. W1/W2 e INFOs registrados abajo; ninguno bloquea.

## Gates de cierre

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias / 0 errores**.
- Suite completa: **2127/2127**, 0 fallas, 0 omitidas (baseline 8.151: 2109; +12 T1, +6 T2).
- Cobertura (`--collect:"XPlat Code Coverage"` + `CommandCenter.Tests/coverage.runsettings` + `python scripts/check-coverage.py`): Core **0.8868** / Sales.Module **0.8962** / Inventory.Module **0.8558** vs umbrales .70/.80/.72 — **exit 0**.
- Spot-check del padre: filtro focal `~CheckoutPreviewRateAnchor` re-ejecutado → **9/9**; revisión estructural de ambos diffs (T1/T2) contra las specs.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-CPR-01 | COMPLIANT | `SalesController.Checkout.cs:32-35` (resolver solo con `ExchangeRate > 0`; fallback `sale.AppliedRate`; guard 400 intacto); `SalesService.Mapping.cs:102-108` (delegación con contextLabel "CheckoutPreview"; privado `(decimal,string,int)` sin cambios); mensaje exacto del rechazo (`Mapping.cs:156`); middleware 400 (`GlobalExceptionHandlerMiddleware.cs:228-244`, registrado en pipeline). |
| REQ-CPR-02 | COMPLIANT (I1) | 9 casos service-level (tolerancia/techo/ancla/rechazo×2/fail-open×2/tolerancia configurable/cancelación) + 3 controller-level nuevos + passthrough en 2 existentes (nombrados). Aserciones exactas. |
| REQ-VMEO-01 | COMPLIANT (W1) | Los 14 sitios con log + notificación (tabla de enumeración completa replicada por el verificador); mensaje exacto del diálogo de metadatos; fallback intacto; ciclo de vida sin cambios. |
| REQ-VMEO-02 | COMPLIANT | 6 tests de logging con markers GUID (incluye cancelación sin log); enumeración estática replicada. |
| S3 (refutación) | COMPLIANT (W2) | `InventoryDbContext.cs:122-131` + migraciones `20260906171036`/`20260908152110` + `ProductConcurrencyTokenTests` (2 tests de modelo ejecutados; gated retorna temprano sin Postgres); catch de `Apply.cs:186` alcanzable. |

## Hallazgos registrados (honestos)

- **W1 (aceptado, fuera de alcance)**: `ProductDialogViewModel.VerifySkuAsync` (L337) tiene un catch operativo pre-existente que loguea pero notifica inline (`SkuVerificationMessage`) en lugar de `IDialogService`; no está en el inventario mandatorio de los 14 sitios, no es regresión y no se corrige por alcance estricto del hallazgo (queda como recomendación).
- **W2 (patrón pre-existente)**: el test gated de conflicto stale de `ProductConcurrencyTokenTests` retorna temprano sin `TEST_POSTGRES_CONNECTION` y cuenta como superado; la refutación SRE-04 se sostiene por config + migraciones + tests de modelo + las corridas con Postgres real de 8.150/8.151.
- **I1**: el passthrough Moq de los 2 tests ajustados usa `ExchangeRate == AppliedRate`; el wiring real lo prueban los tests dedicados.
- **I2**: RED no re-ejecutable desde los commits (impl+tests juntos); aritmética consistente (11F→37/37; 5F→6/6; totales 2121/2127).
- **I3**: cancelación observada solo al entrar en `ResolveCheckoutRateAsync` (privado sin token, limitación pre-existente documentada).
- **I4**: `PosViewModel:403-409` notifica inline (no diálogo) — fuera de alcance; "ya conforme" se sostiene en lo sustancial.
- **I5**: formato de decimales del mensaje de rechazo según cultura corriente (preexistente, compartido con completar venta).

## Artefactos / commits

Change `2026-10-09-audit-new-findings` (proposal, 2 specs, tasks, state, este reporte); tracker `odd/tasks/auditoria-nuevos-hallazgos.md`; ANEXO 8.152. Commits: `e51a86a` (plan + auditoría re-emitida) + `b2e283a` (T1) + `0ddaa62` (T2) + commit de cierre.
