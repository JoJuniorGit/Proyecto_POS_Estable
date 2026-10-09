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
| 3. Verificación + cierre (T3) | suite completa + cobertura (`dotnet test --collect:"XPlat Code Coverage"` + `python scripts/check-coverage.py <cobertura.xml>`, invocación de ANEXOS previos) | Revertir docs |

## T1 (S1) — Anclaje de tasa en checkout-preview (SEC-08)

- [ ] 1.1 RED observable (service-level + controller-level) antes de implementar.
- [ ] 1.2 `ResolveCheckoutRateAsync` público en `ISalesService` + delegación en el resolver privado (contextLabel "CheckoutPreview"); firma/semántica del privado intacta.
- [ ] 1.3 Preview usa el resolver con `ExchangeRate > 0`; fallback `sale.AppliedRate` intacto; guard 400 intacto.
- [ ] 1.4 Tests: dentro de tolerancia / ancla a BCV / rechazo ±100% con mensaje exacto / sin BCV fail-open / tolerancia configurable / ajuste de tests existentes (reportado nombradamente).
- [ ] 1.5 Build 0/0 + focused + suite completa; evidencia RED/GREEN; commit `fix(8.152)` (lo hace el padre).

## T2 (S2) — Observabilidad de catches en ViewModels (CLEAN-06)

- [ ] 2.1 RED observable (tests de logging por archivo) antes de implementar.
- [ ] 2.2 BaseViewModel: L74/L91 → log; OCE y swallows tipados permanecen (razón documentada).
- [ ] 2.3 InventoryViewModel.cs L205/L302/L380 + InventoryViewModel.Operations.cs L32/L59/L80/L128/L153/L189/L228/L250 → log; notificaciones existentes intactas.
- [ ] 2.4 ProductDialogViewModel L249: `Debug.WriteLine` → `ClientStateLogger.LogError` + `_dialogService?.ShowWarning(...)`.
- [ ] 2.5 PosViewModel sin cambios (conforme; documentado). Build 0/0 + focused + suite completa; evidencia RED/GREEN; commit `fix(8.152)` (lo hace el padre).

## T3 (S1-S3) — Verificación independiente + cierre

- [ ] 3.1 Verificador independiente por unidad (read-only): evidencia por archivo/línea; réplica de la enumeración de catches y de los escenarios de tasa; tests existentes ajustados audita­dos.
- [ ] 3.2 Refutación SRE-04 documentada (evidencia pineada; spot-check `ProductConcurrencyTokenTests`).
- [ ] 3.3 Suite completa + cobertura (Core ≥0.70, Sales ≥0.80, Inventory ≥0.72) + build 0/0.
- [ ] 3.4 `verify-report.md` + ANEXO 8.152 en `docs/reporte.txt` + cierre del tracker ODD + commit `docs(8.152)`.

## Notes

- Tracker ODD: `odd/tasks/auditoria-nuevos-hallazgos.md`.
- Los 17 abiertos ya conocidos NO se tocan (roadmap fases 2/3 del ANEXO 8.149).
- Los writers NO commitean; el padre commitea por unidad.
