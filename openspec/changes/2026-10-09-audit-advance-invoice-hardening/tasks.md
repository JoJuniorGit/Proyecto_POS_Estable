# Tasks: Hardening de adelantos y facturas (8.154)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~450-600 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 3 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. SEC-03/04 backend (T1) | `--filter "FullyQualifiedName~CashDrawerCashAdvanceSecurity\|~CashDrawerRateAnchor\|~CancellationPropagation"` | Revertir controller + DTO + tests |
| 2. SEC-03 cliente WPF (T2) | `--filter "FullyQualifiedName~ClientHttpContract\|~CashDrawerClosure\|~CashDrawerRbac"` | Revertir servicios/VM + tests |
| 3. SRE-01 facturas (T3) | `--filter "FullyQualifiedName~SupplierInvoiceApplyOrdering\|~SupplierInvoiceApply"` | Revertir helper + loop + tests |
| 4. Verificación + cierre (T4) | suite completa + cobertura | Revertir docs |

## T1 (S1+S2) — Backend: identidad del token + tasa anclada en adelantos

- [ ] 1.1 `ProcessCashAdvanceAsync`: guard `UserId` → 401 `"Sesión inválida."`; `GetUserAsync` → 403 `"Usuario no encontrado."`; `cashierId/userName` desde el usuario del token. Se elimina el fallback al body.
- [ ] 1.2 `CashAdvanceRequest`: eliminar `CashierId` y `UserName`.
- [ ] 1.3 Anclaje: `anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken)` antes de `_cashAdvanceCoordinator.ProcessAsync(...)`.
- [ ] 1.4 Tests (RED→GREEN): identidad desde el token (body con nombre/cashier falsos es ignorado), 401 sin sesión, 403 usuario inexistente; anclaje (dentro/fuera de tolerancia, rechazo ±100% con mensaje exacto, fail-open). Ajustes de tests existentes reportados (`CancellationPropagationTests`).
- [ ] 1.5 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.154)` (lo hace el padre).

## T2 (S1 cliente WPF) — El cliente deja de enviar identidad

- [ ] 2.1 `ICashDrawerService.ProcessCashAdvanceAsync` sin `cashierId`/`userName`; `CashDrawerService` no incluye `CashierId`/`UserName` en el body; `CashDrawerViewModel` deja de pasarlos.
- [ ] 2.2 Tests: contrato HTTP pincha body sin identidad; mocks/fakes ajustados reportados; suite verde.
- [ ] 2.3 Build 0/0 + focused + suite; commit `fix(8.154)` (lo hace el padre).

## T3 (S3) — Facturas de proveedor: orden determinista

- [ ] 3.1 Helper puro `SupplierInvoiceApplyOrdering.OrderForApply` (archivo propio; asc por `ResolvedProductId`, nulls primero, tie-break `Id`).
- [ ] 3.2 `Apply.cs:82` usa el helper (mismo conjunto de efectos).
- [ ] 3.3 Tests puros (orden, nulls, ties, determinismo con entrada invertida) + regresión de `ConfirmAsync` con líneas desordenadas.
- [ ] 3.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.154)` (lo hace el padre).

## T4 (S1-S3) — Verificación + cierre

- [ ] 4.1 Verificador independiente: identidad del token, anclaje por escenarios, orden del helper, tests ajustados auditados.
- [ ] 4.2 Suite completa + cobertura + build 0/0.
- [ ] 4.3 `verify-report.md` + ANEXO 8.154 + cierre del tracker + commit `docs(8.154)`.

## Notes

- Tracker: `odd/tasks/auditoria-adelantos-facturas.md`.
- Compatibilidad: clientes viejos que envíen los campos eliminados siguen recibiendo 200 (miembros desconocidos ignorados por STJ), pero la identidad sale del token.
- Los writers NO commitean; el padre commitea por unidad.
