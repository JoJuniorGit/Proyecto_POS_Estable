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

- [x] 1.1 Guard de identidad exacto (401 "Sesión inválida." / 403 "Usuario no encontrado."; identidad desde `GetUserAsync` del token). Fallback al body eliminado.
- [x] 1.2 `CashAdvanceRequest` sin `CashierId`/`UserName`.
- [x] 1.3 `anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken)` antes del coordinador.
- [x] 1.4 Tests: identidad token vs body, 401×2, 403, pin DTO/STJ; anclaje 4 escenarios. RED 10F→GREEN 33/33; ajustes reportados (CancellationPropagationTests, ControllerFactory, helper RateAnchor).
- [x] 1.5 Build 0/0 + focused + suite 2155/2155; commit `d7616ac`.

## T2 (S1 cliente WPF) — El cliente deja de enviar identidad

- [x] 2.1 Firma del servicio sin `cashierId`/`userName`; body sin `CashierId`/`UserName`; VM sin los locals; `_userSession` eliminado (sin lecturas ni llamadores).
- [x] 2.2 Pin de body real (arnés `CapturedRequest.Body` extendido) con negativos/positivos; `MockClientCashDrawerService` ajustado (reportado). RED con `"cashierId":null` serializado → GREEN 111/111; suite 2156/2156.
- [x] 2.3 Build 0/0; commit `68e9c6c`.

## T3 (S3) — Facturas de proveedor: orden determinista

- [x] 3.1 Helper puro `SupplierInvoiceApplyOrdering.OrderForApply` (archivo propio; asc `ResolvedProductId`, nulls primero, tie-break `Id`).
- [x] 3.2 `Apply.cs:82` usa el helper (única línea; mismos efectos).
- [x] 3.3 5 tests puros + regresión con orden descendente; RED 4F (stub sin orden) → GREEN 31/31; sin tests existentes tocados.
- [x] 3.4 Build 0/0 + suite 2162/2162; commit `359e50f`.

## T4 (S1-S3) — Verificación + cierre

- [x] 4.1 Verificador independiente: 5/5 requisitos COMPLIANT; focused reproducidos (33/33, 111/111, 31/31); ajustes auditados sin enmascaramiento; W1 (Web envía campos muertos) + INFOs I1-I5.
- [x] 4.2 Suite 2162/2162 + cobertura Core 0.8886 / Sales 0.8962 / Inventory 0.8559 (exit 0) + build 0/0.
- [x] 4.3 `verify-report.md` + ANEXO 8.154 + cierre del tracker + commit `docs(8.154)`.

## Notes

- Tracker: `odd/tasks/auditoria-adelantos-facturas.md`.
- Compatibilidad: clientes viejos que envíen los campos eliminados siguen recibiendo 200 (miembros desconocidos ignorados por STJ), pero la identidad sale del token.
- Los writers NO commitean; el padre commitea por unidad.
