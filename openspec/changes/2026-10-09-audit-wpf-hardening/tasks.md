# Tasks: Hardening WPF + checkout server-authoritative (8.156)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~500-650 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 2 unidades + T2b + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Vistas WPF (T1) | build + `--filter "FullyQualifiedName~LoginViewModelLifecycle\|~ViewModelDisposal"` | Revertir las 5 vistas |
| 2. Checkout WPF (T2) | `--filter "FullyQualifiedName~CheckoutUx\|~CheckoutPreviewClientGate\|~ClientHttpContract"` | Revertir cliente + VM + tests |
| 3. Verificación + cierre (T3) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1+S2) — Vistas WPF: desuscripción y ciclo de vida

- [x] 1.1 `ProductDialog`/`ServerConnectionDialog`: delegado almacenado + `-=` en `OnClosed` antes de `Dispose()`.
- [x] 1.2 `InventoryView`/`LoginView`: patrón `DailyClosureView` (hook/unhook + Loaded/Unloaded); `LoginView` sin dispose del singleton.
- [x] 1.3 T1b: `CreateInvoiceProductDialog` incluido (misma clase con `+=` y sin `-=`); `AuthorizationWaitDialog`/`InterruptedTransactionDialog` ya conformes; Variant dialogs documentados (asignación sobre VM local — fuera de la clase).
- [x] 1.4 Build 0/0 + focused 12/12 + suite 2172/2172; commit `28e32da`.

## T2 (S3) — Checkout WPF consume `/checkout-preview`

- [x] 2.1 Cliente `GetCheckoutPreviewAsync` + DTO + pin de contrato.
- [x] 2.2 Gate espejo de `computeCheckoutGate` (firma fresca + versión monotónica; fail-closed canónico; rounding del servidor; override por `preview.IsFullyPaid`; `RemainingBalance*` del preview; seam `PendingPreview`).
- [x] 2.3 5 tests del gate (RED 3F→GREEN) + 8 `CheckoutUxTests` ajustados reportados.
- [x] 2.4 Build 0/0 + combinado 117/117 + suite 2178/2178; commit `1540a27`.

## T2b (W1/W2/I2 de verificación)

- [x] 2b.1 Mock E2E sirve `checkout-preview` (contrato canónico) + test `E2EMockCheckoutPreviewTests` (RED→GREEN).
- [x] 2b.2 Test del call-path `FinalizeSale`→`CompleteSaleAsync` con el rounding del servidor.
- [x] 2b.3 Checkbox de custodia `IsEnabled="{Binding IsCustodyAllowed}"` (paridad web).
- [x] 2b.4 Build 0/0 + focused 16/16 + suite 2180/2180 + cobertura CON Postgres exit 0; commit `0427309`.

## T3 (S1-S3) — Verificación + cierre

- [x] 3.1 Verificador independiente: 10/10 COMPLIANT; ajustes auditados sin enmascaramiento; W1/W2/I1/I2 registrados (W1/W2/I2 cerrados por T2b).
- [x] 3.2 Suite CON Postgres 2178 → 2180 post-T2b; cobertura 0.8886/0.9057/0.8621 (exit 0); build 0/0.
- [x] 3.3 `verify-report.md` + ANEXO 8.156 + cierre del tracker + commit `docs(8.156)`.

## Notes

- Tracker: `odd/tasks/auditoria-wpf-hardening.md`.
- Web sin cambios (ya conforme 8.5-WEB1, sin bundle).
- Los writers NO commitean; el padre commitea por unidad.
