# Tasks: Limpieza de controladores + cálculo de cobro + PERF-03 (8.159)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~700-1000 |
| Budget risk | High |
| Chained PRs | Yes (continúa la cadena V0.15; slices sugeridos por unidad) |
| Split | 4 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. CLEAN-04 (T1) | `--filter "FullyQualifiedName~Phase4FinancialIntegrityAndPreview\|~CheckoutPreviewRateAnchor\|~CheckoutCalculation"` | Revertir servicio + controller + DI + tests |
| 2. CLEAN-01 grupo A (T3) | `--filter "FullyQualifiedName~Phase3Authentication\|~PaymentMethod\|~Settings\|~Users\|~Auth"` | Revertir 8 controladores A + sus tests |
| 3. CLEAN-01 grupo B (T4) | `--filter "FullyQualifiedName~CashDrawer\|~DailyClosure\|~Products\|~Sales"` | Revertir controladores B + sus tests |
| 4. Verificación + cierre (T5) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1) — CLEAN-04: extraer el cálculo de cobro

- [ ] 1.1 `ICheckoutCalculationService` + `CheckoutCalculationService` (fetch vía `ISalesService.GetSaleAsync`, anclaje vía `ResolveCheckoutRateAsync` cuando `ExchangeRate > 0`, matemática espejo exacta; `rate <= 0` → `ArgumentException("Tasa de cambio inválida.")`).
- [ ] 1.2 Endpoint `GetCheckoutPreviewAsync`: auth + `var preview = await _checkoutCalculator.CalculatePreviewAsync(id, request, cancellationToken); return Ok(preview);`; ctor con param opcional; DI scoped en `ServiceCollectionExtensions` (junto a L65-95).
- [ ] 1.3 Tests: existentes de preview ajustados para construir el servicio REAL con su mock (reportados); nuevos tests del servicio (redondeo/vuelto/pagos mixtos/rate inválida).
- [ ] 1.4 Build 0/0 + focused + suite; commit `refactor(8.159)` (lo hace el padre).

## T2 (S3) — PERF-03: refutación + residual

- [ ] 2.1 `AsNoTracking` en el primer fetch de `ConfirmPickupAsync` (solo lectura); evidencia de refutación (AsSplitQuery desde 148e6a3; History con AsNoTracking) documentada en el ANEXO.
- [ ] 2.2 Build + focused + suite; commit (padre).

## T3 (S2-A) — CLEAN-01 grupo A (37 wrappers)

- [ ] 3.1 Eliminar los `[NonAction]` de: Auth(4), Users(9), PaymentMethods(6), Settings(8), ExchangeRate(4), Health(2), Receipts(1), Reservations(3).
- [ ] 3.2 Migrar los call sites de tests de esos wrappers a `*Async` (token `CancellationToken.None`/default); reportar la lista de archivos de tests tocados.
- [ ] 3.3 Build 0/0 + focused + suite; commit `refactor(8.159)` (lo hace el padre).

## T4 (S2-B) — CLEAN-01 grupo B (56 wrappers)

- [ ] 4.1 Eliminar los `[NonAction]` de: CashDrawer(9), DailyClosure(3), Products(9)+ImportExport(3)+Variants(5), SalesController(6)+Checkout(4)+Claims(2)+Customers(6)+History(3)+HoldOrders(6).
- [ ] 4.2 Migrar los call sites de tests (incl. `CancellationPropagationTests` L251-266 → nombres `*Async`) y los usos en `tests/` si los hay; reportar la lista.
- [ ] 4.3 Build 0/0 + focused + suite; commit `refactor(8.159)` (lo hace el padre).

## T5 (S1-S3) — Verificación + cierre

- [ ] 5.1 Verificador independiente: comportamiento del preview idéntico (mensajes/status), cero wrappers residuales (grep 93→0), call sites migrados sin debilitar aserciones.
- [ ] 5.2 Suite completa (con Postgres) + cobertura + build 0/0.
- [ ] 5.3 `verify-report.md` + ANEXO 8.159 + cierre del tracker + commit `docs(8.159)`.

## Notes

- Tracker: `odd/tasks/auditoria-limpieza-controladores.md`.
- 8.158 (SRE-03/CLEAN-03) queda pendiente; este tramo no lo toca.
- Los writers NO commitean; el padre commitea por unidad.
