# Tasks: Hardening WPF + checkout server-authoritative (8.156)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~500-650 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 2 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Vistas WPF (T1) | build + `--filter "FullyQualifiedName~LoginViewModelLifecycle\|~ViewModelDisposal"` | Revertir las 4 vistas |
| 2. Checkout WPF (T2) | `--filter "FullyQualifiedName~CheckoutUx\|~CheckoutPreviewClientGate\|~ClientHttpContract"` | Revertir cliente + VM + tests |
| 3. Verificación + cierre (T3) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1+S2) — Vistas WPF: desuscripción y ciclo de vida

- [ ] 1.1 `ProductDialog` y `ServerConnectionDialog`: delegado almacenado + `-=` en `OnClosed` antes de `Dispose()` (sin lambdas vivas).
- [ ] 1.2 `InventoryView` y `LoginView`: patrón `DailyClosureView` (hook/unhook en `Loaded/Unloaded` + `DataContextChanged`, `_boundViewModel`); `LoginView` NO dispone el VM.
- [ ] 1.3 Build 0/0 + focused de ciclo de vida verde; commit `fix(8.156)` (lo hace el padre).

## T2 (S3) — Checkout WPF consume `/checkout-preview`

- [ ] 2.1 `ISalesService`/`SalesService`: `GetCheckoutPreviewAsync` + DTO cliente (en el archivo de `SalePaymentDto`) + pin de contrato HTTP.
- [ ] 2.2 `CheckoutViewModel`: gate espejo del web (`IsPreviewFresh` por firma + versión monotónica; `IsFullLiquidation`, `IsCustodyAllowed`, `CanFinalize`, `RoundingAdjustment`, `RemainingBalance*` desde el preview fresco; fallo → bloqueo + mensaje exacto del web; branch override por `preview.IsFullyPaid`; `RoundingAdjustment` del preview a `CompleteSaleAsync`). Seam determinista para tests.
- [ ] 2.3 Tests: nuevos espejo del gate (fresco/estancado/fallido/override/normal) + `CheckoutUxTests` ajustados (reportados nominados).
- [ ] 2.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.156)` (lo hace el padre).

## T3 (S1-S3) — Verificación + cierre

- [ ] 3.1 Verificador independiente: gate WPF vs `computeCheckoutGate` del web, vistas sin retención, tests ajustados auditados.
- [ ] 3.2 Suite completa (con Postgres) + cobertura + build 0/0.
- [ ] 3.3 `verify-report.md` + ANEXO 8.156 (incl. web verificado como ya-conforme) + cierre del tracker + commit `docs(8.156)`.

## Notes

- Tracker: `odd/tasks/auditoria-wpf-hardening.md`.
- El Web NO se toca (ya consume el preview con gate testeado desde 8.5-WEB1); evidencia en el ANEXO.
- Los writers NO commitean; el padre commitea por unidad.
