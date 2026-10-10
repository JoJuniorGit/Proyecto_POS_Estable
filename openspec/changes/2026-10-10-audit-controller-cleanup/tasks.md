# Tasks: Limpieza de controladores + cálculo de cobro + PERF-03 (8.159)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~700-1000 |
| Budget risk | High |
| Chained PRs | Yes (continúa la cadena V0.15; slices por unidad) |
| Split | 4 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. CLEAN-04 (T1) | `--filter "FullyQualifiedName~Phase4FinancialIntegrityAndPreview\|~CheckoutPreviewRateAnchor\|~CheckoutCalculation"` | Revertir servicio + controller + DI + tests |
| 2. CLEAN-01 grupo A (T3) | `--filter "FullyQualifiedName~Phase3Authentication\|~PaymentMethod\|~Settings\|~Auth"` | Revertir 8 controladores A + sus tests |
| 3. CLEAN-01 grupo B (T4) | `--filter "FullyQualifiedName~CashDrawer\|~DailyClosure\|~Products\|~Sales\|~CancellationPropagation"` | Revertir controladores B + sus tests |
| 4. Verificación + cierre (T5) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1) — CLEAN-04: extraer el cálculo de cobro

- [x] 1.1 `ICheckoutCalculationService` + `CheckoutCalculationService` (matemática verbatim + anclaje SEC-08; `rate <= 0` → ArgumentException; missing → KeyNotFound del `GetSaleAsync`).
- [x] 1.2 Endpoint = auth + delegación + `Ok`; ctor con param opcional; DI scoped.
- [x] 1.3 5 constructores de tests ajustados (reportados) + 9 tests nuevos del servicio; RED compile-level → GREEN.
- [x] 1.4 Build 0/0 + focused 28/28 + suite 2216/2216; commit `d52a70f`.

## T2 (S3) — PERF-03: refutación + residual

- [x] 2.1 `AsNoTracking` en el primer fetch de `ConfirmPickupAsync`; evidencia de refutación (AsSplitQuery desde `148e6a3`; History con AsNoTracking).
- [x] 2.2 Focused 62/62 + suite 2216/2216; commit `e4c2401`.

## T3 (S2-A) — CLEAN-01 grupo A (37 wrappers)

- [x] 3.1 Eliminados 37 wrappers (Auth/Users/PaymentMethods/Settings/ExchangeRate/Health/Receipts/Reservations).
- [x] 3.2 72 call sites migrados en 17 archivos de test (RED 72 CS1061 → GREEN).
- [x] 3.3 Build 0/0 + focused 478/478 + suite 2216/2216; commit `aaa01ce`.

## T4 (S2-B) — CLEAN-01 grupo B (56 wrappers)

- [x] 4.1 Eliminados 56 wrappers (CashDrawer/DailyClosure/Products×3/Sales×7); `[NonAction]`=0 global.
- [x] 4.2 114 call sites migrados en 22 archivos (RED 114 errores → GREEN); `CancellationPropagationTests` lista los `*Async`.
- [x] 4.3 Build 0/0 + focused 356/356 + suite 2216/2216; commit `11ef283`.

## T5 (S1-S3) — Verificación + cierre

- [x] 5.1 Verificador independiente: 3/3 COMPLIANT; comparación mecánica; equivalencia 1:1 (0 mismatches); W1/W2 + I1-I5 registrados.
- [x] 5.2 Suite CON Postgres 2216/2216 + cobertura Core 0.8886 / Sales 0.9065 / Inventory 0.8621 (exit 0) + build 0/0.
- [x] 5.3 `verify-report.md` + ANEXO 8.159 + cierre del tracker + commit `docs(8.159)`.

## Notes

- Tracker: `odd/tasks/auditoria-limpieza-controladores.md`.
- 8.158 (SRE-03/CLEAN-03) queda como ÚNICO tramo pendiente del roadmap.
- Los writers NO commitean; el padre commitea por unidad.
