# Tasks: Hallazgos nuevos de la auditoría re-emitida (8.152)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~250-420 |
| Budget risk | Medium |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 2 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. SEC-08 backend (T1) | `--filter "FullyQualifiedName~Phase4FinancialIntegrityAndPreview\|FullyQualifiedName~FinancialRobustness\|FullyQualifiedName~CheckoutPreviewRateAnchor"` | Revertir los archivos backend + tests |
| 2. CLEAN-06 WPF (T2) | `--filter "FullyQualifiedName~ViewModelCatchLogging\|~InventoryViewModel\|~ProductDialog"` | Revertir los archivos WPF + tests |
| 3. Verificación + cierre (T3) | suite completa + cobertura (coverage.runsettings + `python scripts/check-coverage.py <cobertura.xml>`) | Revertir docs |

## T1 (S1) — Anclaje de tasa en checkout-preview (SEC-08)

- [x] 1.1 RED observable antes de implementar (11 fallas en el filtro focal).
- [x] 1.2 `ResolveCheckoutRateAsync` público en `ISalesService` + delegación en el resolver privado (contextLabel "CheckoutPreview"); firma/semántica del privado intacta (verificado por diff).
- [x] 1.3 Preview usa el resolver con `ExchangeRate > 0`; fallback `sale.AppliedRate` intacto; guard 400 intacto; sin catch de `ArgumentException`.
- [x] 1.4 Tests: tolerancia/techo/ancla/rechazo×2/fail-open×2/tolerancia configurable/cancelación + 3 controller-level; ajuste de 2 tests existentes (passthrough) reportado nombradamente.
- [x] 1.5 Build 0/0 + focused 37/37 + suite 2121/2121; commit `b2e283a`.

## T2 (S2) — Observabilidad de catches en ViewModels (CLEAN-06)

- [x] 2.1 RED observable (5 fallas / 6 tests) antes de implementar.
- [x] 2.2 BaseViewModel: L75/L92 → log; OCE y swallows tipados permanecen (razón documentada).
- [x] 2.3 InventoryViewModel.cs L206/L304/L383 + InventoryViewModel.Operations.cs (8 sitios) → log; notificaciones existentes intactas.
- [x] 2.4 ProductDialogViewModel L249-252: `Debug.WriteLine` → `ClientStateLogger.LogError` + `_dialogService?.ShowWarning("Error de Metadatos", "No se pudieron cargar los datos auxiliares del producto. Verifique la conexión.")`; fallback intacto.
- [x] 2.5 PosViewModel sin cambios (conforme; documentado). Build 0/0 + focused 6/6 + suite 2127/2127; commit `0ddaa62`.

## T3 (S1-S3) — Verificación independiente + cierre

- [x] 3.1 Verificador independiente read-only: PASS WITH WARNINGS; 4/4 requisitos COMPLIANT; enumeración de catches y escenarios de tasa replicados; tests ajustados auditados.
- [x] 3.2 Refutación SRE-04 confirmada (config + migraciones + `ProductConcurrencyTokenTests`; gated sin Postgres registrado como W2).
- [x] 3.3 Suite 2127/2127 + cobertura Core 0.8868 / Sales 0.8962 / Inventory 0.8558 (exit 0) + build 0/0.
- [x] 3.4 `verify-report.md` + ANEXO 8.152 + cierre del tracker ODD + commit `docs(8.152)`.

## Notes

- Tracker ODD: `odd/tasks/auditoria-nuevos-hallazgos.md`.
- Los 17 abiertos ya conocidos NO se tocan (roadmap fases 2/3 del ANEXO 8.149).
- Los writers NO commitean; el padre commitea por unidad.
