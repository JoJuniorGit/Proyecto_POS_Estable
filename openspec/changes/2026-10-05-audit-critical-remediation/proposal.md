# Proposal: Audit Critical Remediation

## Intent

Remediate the five **critical, independently verified** findings of the 2026-10-05 integral audit (`docs/auditoria_integral_fases_1_4.txt`) that are absent from the code despite the audit's matrix marking them as done: server-authoritative checkout rounding (SEC-01), cash-advance price-authorization bypass (SEC-02), non-deterministic stock-deduction lock order (SRE-01), missing idempotency on item/cash/closure mutations (SRE-02), and silent cart commit failures without rollback (CLEAN-02).

## Scope

- **In scope**: the five fixes above, with tests, on branch `V0.15`.
- **Out of scope**: the other 15 verified findings — each phase gets its own change (roadmap at the end).
- **Affected projects and project-local commands**:
  - Backend: `Sales.Module`, `Inventory.Module`, `Backend.API` → `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`
  - Desktop: `Desktop.Client.Core` (+ Web for SRE-02 client keys) → same .NET suite; `npm test` in `Web.Frontend`
  - Build gate: `dotnet build CommandCenter.slnx -c Release` (0/0)

## Approach (one line per finding)

- **SEC-01**: ignore the client `RoundingAdjustment` at completion; recompute after payments attach using the preview formula against persisted `sale.Payments` (mirror `SalesController.Checkout.cs:63`).
- **SEC-02**: reject `IsCashAdvance` products in `AddItemAsync` and `UpdateSaleItemsAsync` with an exact message; the coordinator path (`CreateCashAdvanceSaleAsync`) stays untouched.
- **SRE-01**: pre-resolve targets (parent/conversion), consolidate by `(target, reason, saleId)` summing quantities, execute ordered ascending by target id; pure helper for unit tests.
- **SRE-02**: extract `ResolveIdempotencyAsync` into a shared resolver; require `Idempotency-Key` on POST items / cash transaction / daily closure with replay semantics; unique `ClosureDate` index + in-transaction duplicate guard; idempotent cancel (200 on already cancelled); update WPF and Web clients to send keys.
- **CLEAN-02**: replace the silent catches with `ClientStateLogger.LogError` + server re-sync (`GetSaleAsync`) + `IDialogService` notification; log the three best-effort empty catches.

## Risks

- Rounding semantics must mirror the preview **exactly** (thresholds 0.05/0.01) or valid sales break — pinned by tests including partial payments and mixed currencies.
- Required keys on the three endpoints are a breaking API change until clients ship — server + clients ship in the same chain; fail-closed 400 documented.
- Unique `ClosureDate` index can fail on pre-existing duplicate data — verify dev DB cleanliness before apply; migration Down drops the index.
- Consolidation changes movement granularity for identical `(target, reason, saleId)` requests — per-target totals preserved; affected tests updated deliberately.
- The ordering change touches every sale's hot path — covered by focused + full suite; the only schema change is the closure index.

## Rollback

Revert the work units (stacked feature branch); the closure-index migration Down drops the index. No data rewrites; no production data touched by tests (isolated `pos_test`/SQLite).

## Roadmap (out of scope)

- **Phase 2 (high)**: SEC-03, SEC-05, SEC-06, SEC-07; PERF-01, PERF-02, PERF-04, PERF-05.
- **Phase 3 (medium/refactor)**: SRE-03; CLEAN-01, CLEAN-03, CLEAN-04, CLEAN-05; PERF-03.

## Delivery

`ask-on-risk`; chain **stacked-to-main** (cached V0.15 chain policy); work-unit commits per fix. ANEXO 8.149.
