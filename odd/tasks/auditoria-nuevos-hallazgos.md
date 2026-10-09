# Hallazgos nuevos de la auditoría integral re-emitida (SEC-08, CLEAN-06, SRE-04)

Objetivo: corregir los hallazgos NUEVOS de la auditoría `docs/auditoria_integral_fases_1_4.txt` re-emitida 2026-10-08 (SEC-08 anclaje de tasa en preview; CLEAN-06 catches mudos en ViewModels; SRE-04 refutado con evidencia). Los 17 abiertos ya conocidos quedan fuera (roadmap Fases 2/3 del ANEXO 8.149). Change: `openspec/changes/2026-10-09-audit-new-findings/` (ANEXO 8.152). Branch: V0.15. Entrega: ask-on-risk → cadena stacked-to-main (cacheada). RDD: off (clone-local). Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py` (Core ≥0.70, Sales ≥0.80, Inventory ≥0.72).

## Specs

- **S1 (SEC-08 — anclaje de tasa en checkout-preview)**: `POST /api/sales/{id}/checkout-preview` resuelve la tasa con la MISMA semántica del completar venta vía `ISalesService.ResolveCheckoutRateAsync(id, clientRate, ct)` (delegación al resolver privado existente, contextLabel "CheckoutPreview"): desvío ≤ tolerancia (`RateDeviationTolerancePct`, default 0.10) → tasa cliente; > tolerancia → BCV anclada; ≥ ±100% → `ArgumentException` (middleware global → 400) con mensaje exacto "La tasa de cambio {rate} fue rechazada: excede ±100% de la tasa BCV oficial ({official}). Contacte al supervisor."; sin BCV → fail-open; `ExchangeRate <= 0` → `sale.AppliedRate` (intacto). Detalle: `openspec/changes/2026-10-09-audit-new-findings/specs/checkout-preview-rate-anchor/spec.md`.
- **S2 (CLEAN-06 — catches operativos con log/notificación)**: 14 sitios exactos en BaseViewModel (2), InventoryViewModel.cs (3) + InventoryViewModel.Operations.cs (8) y ProductDialogViewModel (1) registran `ClientStateLogger.LogError($"<contexto>: {ex.Message}", nameof(<VM>))` y conservan/proveen notificación por `IDialogService`; swallows tipados de ciclo de vida (ObjectDisposed/OperationCanceled/SemaphoreFull) se preservan documentados; `PosViewModel` ya conforme. Detalle: `openspec/changes/2026-10-09-audit-new-findings/specs/viewmodel-error-observability/spec.md`.
- **S3 (SRE-04 — refutación verificada)**: el token `xmin` de `Product` YA existe (`Inventory.Module/Data/InventoryDbContext.cs:122-131`, migraciones `20260906171036_AddXminConcurrencyTokenToProduct` y `20260908152110_RemoveRowVersionFromProduct`, `ProductConcurrencyTokenTests`); el catch de `SupplierInvoiceService.Apply.cs:186` NO es código muerto. Sin cambio de código; se documenta con evidencia pineada y spot-check del test de modelo.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1 | delegated (writer) | SEC-08: `ResolveCheckoutRateAsync` público + preview anclado + tests (servicio y controller) | pendiente |
| T2 | S2 | delegated (writer) | CLEAN-06: logging/notificación en los 14 catches + tests (log de archivo) | pendiente |
| T3 | S1-S3 | delegated (verify) + inline | Verificación independiente + refutación SRE-04 + suite/cobertura + ANEXO 8.152 + cierre | pendiente |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Corrige los nuevos hallazgos de "auditoria_integral_fases_1_4.txt"".
- L2 (2026-10-09) — Alcance fijado: los 3 hallazgos genuinamente nuevos de la re-emisión 2026-10-08 (SEC-08, CLEAN-06, SRE-04) — los únicos ausentes del roadmap Fases 2/3 del ANEXO 8.149. Verificación previa estática: **SEC-08 CONFIRMADO** (`SalesController.Checkout.cs:30` sin anclaje vs. completar `SalesService.Checkout.cs:98` → `SalesService.Mapping.cs:104`); **CLEAN-06 CONFIRMADO** con referencias de línea con drift (14 sitios operativos reales; `PosViewModel` ya conforme; swallows tipados de ciclo de vida fuera del hallazgo); **SRE-04 REFUTADO** (`InventoryDbContext.cs:122-131` configura xmin + migraciones + `ProductConcurrencyTokenTests`; suite 2038/2038 con Postgres real en 8.151). Plan: proposal/specs/tasks/state en `openspec/changes/2026-10-09-audit-new-findings/`; auditoría re-emitida commiteada en `docs/`.
- L3 (2026-10-09) — Próximo: T1 delegado a writer (test-first estricto; superficies: ISalesService, SalesService.Mapping, SalesController.Checkout, Phase4FinancialIntegrityAndPreviewTests, FinancialRobustnessTests, CheckoutPreviewRateAnchorTests).
