# Proposal: Hardening de adelantos y facturas de proveedor (SEC-03, SEC-04, SRE-01) — ANEXO 8.154

## Contexto

Tramo 2 autorizado por el mantenedor ("Procede con el tramo 8.154"). Alcance: los hallazgos **SEC-03** (suplantación de auditoría en adelantos), **SEC-04** (tasa sin anclaje en adelantos) y **SRE-01** en su parte abierta (orden determinista al aplicar facturas de proveedor). SEC-07 y PERF quedan para tramos siguientes.

Verificación previa (estática, de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| SEC-03 | CONFIRMADO | `CashDrawerController.cs:340-352`: fallback `cashierId = request.CashierId` y `userName = request.UserName` (cualquier string del body); `CashAdvanceRequest` (Sales.Module/DTOs/CashDrawerRequestDtos.cs:25-35) expone `CashierId`/`UserName`. El resultado se persiste en descripciones de `CashTransactions`. |
| SEC-04 | CONFIRMADO | `CashDrawerController.cs:354-363` pasa `request.ExchangeRate` cruda al coordinador. `SalesService.CashAdvance.cs:98` ancla la VENTA de adelanto, pero la comisión y los movimientos envelope usan la tasa recibida → anclar antes en el controller cierra el residual (espejo de `AddTransaction`). |
| SRE-01 (facturas) | CONFIRMADO | `SupplierInvoiceService.Apply.cs:82`: `foreach (var line in invoice.Lines.OrderBy(candidate => candidate.Id))` — el orden de bloqueo de `Products` sigue el Id de línea, no el producto → AB-BA posible entre facturas con mismos productos en orden invertido. Fix solicitado: ordenar por `ResolvedProductId`. |

## Alcance

1. **S1 (SEC-03)** — la identidad del adelanto se resuelve SOLO del token: `UserId` no parseable → 401 `"Sesión inválida."`; usuario inexistente → `UnauthorizedAccessException("Usuario no encontrado.")` (→403); `cashierId = user.Id`, `userName = user.Name`. Se eliminan `CashierId`/`UserName` de `CashAdvanceRequest` y del cliente WPF (propiedades extra de clientes viejos quedan ignoradas por STJ — compatible).
2. **S2 (SEC-04)** — la tasa del adelanto se ancla con la semántica A5 existente ANTES de invocar al coordinador: `ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken)` (≤ tolerancia → cliente; > tolerancia → BCV; ≥ ±100% → rechazo; sin BCV → fail-open).
3. **S3 (SRE-01)** — orden determinista del loop de aplicación: helper puro `SupplierInvoiceApplyOrdering.OrderForApply` (ascendente por `ResolvedProductId`, nulls primero, tie-break por `Id`) usado por `ConfirmAsync`; mismo conjunto de efectos.

Fuera de alcance: SEC-07, PERF-01/02/03/04/05, CLEAN-01/03/04/05 y los refutados SRE-04/SRE-05.

## Enfoque

- S1 backend: guard exacto de la auditoría en `ProcessCashAdvanceAsync`; `IUserService.GetUserAsync` ya existe. S1 cliente: `ICashDrawerService.ProcessCashAdvanceAsync` pierde los parámetros `cashierId`/`userName`; el VM deja de pasarlos.
- S2: una línea + tests de anclaje extendiendo `CashDrawerRateAnchorTests` (arnés A5 existente).
- S3: helper público en archivo propio (precedente `StockDeductionConsolidator`, 8.149) + tests puros dedicados; el loop pasa a `OrderForApply(invoice.Lines)`.
- El contrato HTTP cambia de forma compatible: clientes que aún envíen `cashierId`/`userName` reciben 200 (System.Text.Json ignora miembros desconocidos por defecto) pero esos valores ya no se usan.

## Entrega y riesgos

- Estrategia: `ask-on-risk` → cadena `stacked-to-main` (cacheada). Forecast ~450-600 líneas (backend + cliente WPF + facturas + tests ajustados).
- Riesgo principal: los tests del controller que invocan `ProcessCashAdvance` ahora requieren `UserId` + `GetUserAsync` mockeados (ajustes reportados); el test de cancelación re-ordena su setup.
- RDD: off (clone-local) → self-verification de writers + verificador independiente + suite/cobertura.

## Tramos siguientes (indicativos)

- 8.155: PERF-01 (N+1 staging) + PERF-02 (apply; cuestión de diseño vs savepoint xmin deliberado).
- 8.156: PERF-04/05 + CLEAN-05 (WPF/clientes). 8.157: SEC-07 (SignalR). 8.158: SRE-03 + CLEAN-03. 8.159: CLEAN-01/04 + PERF-03.
