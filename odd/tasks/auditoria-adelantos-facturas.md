# Hardening de adelantos y facturas de proveedor (SEC-03, SEC-04, SRE-01-facturas)

Objetivo: tramo 8.154 del roadmap de la auditoría re-emitida — SEC-03 (identidad del adelanto solo desde el token), SEC-04 (tasa anclada antes del coordinador) y SRE-01 en facturas (orden determinista por `ResolvedProductId` al aplicar). Change: `openspec/changes/2026-10-09-audit-advance-invoice-hardening/` (ANEXO 8.154). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (SEC-03 — identidad del adelanto desde el token)**: `ProcessCashAdvanceAsync` resuelve `cashierId/userName` vía `UserId` → `IUserService.GetUserAsync` (401 "Sesión inválida." sin sesión parseable; 403 "Usuario no encontrado." si no existe); `CashAdvanceRequest` pierde `CashierId`/`UserName`; el cliente WPF deja de enviarlos (compatibilidad: miembros extra ignorados por STJ). Detalle: `specs/cash-advance-identity-anchor/spec.md`.
- **S2 (SEC-04 — tasa anclada)**: `anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, ct)` antes de `_cashAdvanceCoordinator.ProcessAsync(...)` (≤ tolerancia → cliente; > tolerancia → BCV; ≥±100% → rechazo con mensaje exacto; sin BCV → fail-open). Detalle: mismo spec.
- **S3 (SRE-01 facturas — orden determinista)**: helper puro `SupplierInvoiceApplyOrdering.OrderForApply` (asc por `ResolvedProductId`, nulls primero, tie-break `Id`) usado en `Apply.cs:82`; mismos efectos, solo cambia el orden de bloqueos. Detalle: `specs/supplier-invoice-apply-ordering/spec.md`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S2 | delegated (writer) | Backend: guard de identidad + DTO sin campos + anclaje + tests | hecho — RED 10F→GREEN 33/33; suite 2155/2155; 3 ajustes reportados; commit d7616ac |
| T2 | S1 | delegated (writer) | Cliente WPF: deja de enviar identidad + tests | hecho — RED de body real→GREEN 111/111; suite 2156/2156; commit 68e9c6c |
| T3 | S3 | delegated (writer) | Facturas: helper de orden + loop + tests | hecho — RED 4F→GREEN 31/31; suite 2162/2162; commit 359e50f |
| T4 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.154 + cierre | hecho — PASS WITH WARNINGS (5/5 COMPLIANT; W1 web + I1-I5); cobertura 0.8886/0.8962/0.8559 exit 0; ANEXO 8.154; commit de cierre en L5 |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Procede con el tramo 8.154".
- L2 (2026-10-09) — Verificación previa estática: **SEC-03 CONFIRMADO** (`CashDrawerController.cs:340-352` fallback al body + `CashAdvanceRequest` con `CashierId`/`UserName`); **SEC-04 CONFIRMADO** (`:354-363` tasa cruda; la venta ya ancla en `SalesService.CashAdvance.cs:98`, pero comisión/movimientos envelope usan la tasa cliente); **SRE-01-facturas CONFIRMADO** (`Apply.cs:82` orden por `Id` de línea). Evidencia de arneses: `CashDrawerRateAnchorTests` (A5) extensible; `CancellationPropagationTests:223` a ajustar (compila con el DTO viejo + necesita UserId/GetUserAsync); helper puro con precedente `StockDeductionConsolidator` (público, archivo propio, tests dedicados); `ApiUnauthorized` existe; el mock E2E no depende del body.
- L3 (2026-10-09) — Próximo: T1 delegado a writer (test-first; superficies backend: CashDrawerController, CashDrawerRequestDtos, tests de seguridad de adelanto + RateAnchor + CancellationPropagation).
- L4 (2026-10-09) — T1 (d7616ac), T2 (68e9c6c) y T3 (359e50f) completados por writers (test-first con RED observado en cada unidad; suites 2144→2155→2156→2162) + verificación independiente read-only PASS WITH WARNINGS: REQ-CAI-01/02/03 y REQ-SIA-01/02 COMPLIANT; focused reproducidos exactos por el verificador (33/33, 111/111, 31/31); ajustes auditados sin enmascaramiento (CancellationPropagationTests/ControllerFactory/helper RateAnchor/MockClientCashDrawerService/arnés de contrato). W1: el cliente Web React (`CashAdvanceModal.jsx`) sigue enviando cashierId/userName — sin impacto (el server los ignora), limpieza pendiente (G1 + regenerar bundle wwwroot). INFOs I1-I5 en el ANEXO. Cobertura Core 0.8886 / Sales 0.8962 / Inventory 0.8559 (exit 0).
- L5 (2026-10-09) — Cierre: verify-report.md + ANEXO 8.154 + state/tasks actualizados + commit de cierre (hash en git log). Entrega pendiente: push/PR/merge (decisión del mantenedor; cadena stacked-to-main cacheada). Próximo tramo: 8.155 (PERF-01/02) cuando se autorice.
