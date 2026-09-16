# Tasks: cash-closure-integrity

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 500–650 |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 (Slice 1: ~200 lines) → PR 2 (Slice 2: ~300 lines) → PR 3 (Slice 3: ~30 lines) |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | H-01 session-anchored window + preview validation | PR 1 | `dotnet test --filter "ClosureWindowResolver"` | N/A — pure unit tests, no DbContext | `ClosureWindowResolver.cs`, `DailyClosureService.cs` changes only |
| 2 | H-02 CashAdvanceCoordinator + envelope + locator removal | PR 2 | `dotnet test --filter "CashAdvanceCoordinator"` | N/A — EF InMemory for unit; `TEST_POSTGRES_CONNECTION` for envelope assertions | `CashAdvanceCoordinator.cs`, `CashDrawerService.cs` locator removal, controller/DI wiring |
| 3 | Classifier swap in ShiftsController | PR 3 | `dotnet test --filter "PaymentMethodCurrency"` | N/A | `ShiftsController.cs` ~30-line swap |

## Phase 1: Foundation — ClosureWindowResolver (Slice 1)

- [x] 1.1 Create `Sales.Module/Services/ClosureWindowResolver.cs` — pure static method returning `(DateTime StartUtc, DateTime EndExclusiveUtc)`; inputs: `dateUtc`, `activeSessionOpenedAtUtc?`, `lastClosureDateUtc?`. Precedence: `activeSessionOpenedAtUtc ?? lastClosureDateUtc ?? startOfDayUtc` (AD-1, AD-3). Backdated guard: if `StartUtc >= EndExclusiveUtc`, fall back to `lastClosureDateUtc ?? startOfDayUtc` bounded below `EndExclusiveUtc`.
- [x] 1.2 Create `CommandCenter.Tests/Unit/ClosureWindowResolverTests.cs` — test scenarios: "Open session anchors the window" (session at 20:00, last closure 23:00, query 00:15 → start = session OpenedAt); "No session falls back to last closure, then start of day"; "Row exactly at the window start" (>= included); "Row exactly at the right bound" (< excluded); "Backdated closure behind the current session" (empty window falls back, start < EndExclusiveUtc); "Resolver is unit-testable without a database". Covers spec: `cash-closure-arqueo-window` all 5 requirements.
- [x] 1.3 Modify `Sales.Module/Services/DailyClosureService.cs:24-64` — read active `CashDrawerSession` (Status == Open) from `_context.CashDrawerSessions` (AD-2); call `ClosureWindowResolver.Resolve(dateUtc, session?.OpenedAt, lastClosure?.ClosureDate)`; replace `effectiveStartTime` and both predicates (`>` → `>= StartUtc`, keep `< endOfDayUtc`). Covers spec: "Session-Anchored Window Precedence", "Half-Open Interval Boundaries".
- [x] 1.4 Modify `Backend.API/Controllers/DailyClosureController.cs:54` — validate `dateUtc` query parameter: if `== default` or omitted → HTTP 400. Covers spec: "Preview Requires an Explicit Date" scenario "Preview rejects a missing date".
- [x] 1.5 Create `CommandCenter.Tests/Unit/DailyClosureServiceWindowTests.cs` — window integration tests on EF InMemory: verify sales from 20:00–23:59:59 are included in 00:15 arqueo; verify persisted `ClosureDetail.ExpectedAmountBsS` is never rewritten. Covers spec: "A persisted closure is never rewritten".

## Phase 2: Core Implementation — CashAdvanceCoordinator (Slice 2)

- [ ] 2.1 Create `Sales.Module/Services/CashAdvanceCoordinator.cs` — scoped service injecting `SalesDbContext`, `ISalesService`, `ICashDrawerService`, `ISystemSettingsService` (AD-4). Constructor resolves commission via `ISystemSettingsService` — no fallback. Fail-closed: if commission missing/invalid → throw `InvalidOperationException` (AD-6). Relocate the existing execution-strategy envelope from `CashDrawerService.cs:514-519` with default isolation (NOT Serializable) into this coordinator's `ProcessAsync`. Ordering: (1) create accounting sale via `ISalesService.CreateCashAdvanceSaleAsync` with `existingTransaction`, (2) anchor `AppliedRate` from sale, (3) record drawer expense + commission income using anchored rate. Snapshots: persist `AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS`; `RoundingAdjustment` stays default.
- [ ] 2.2 Create `CommandCenter.Tests/Unit/CashAdvanceCoordinatorTests.cs` — test scenarios: "Configured commission is applied"; "Missing commission rejects the advance" (no payout, no sale persisted); "Drawer uses the sale's anchored rate" (AppliedRate anchors movements); "Snapshots are written as before" (RoundingAdjustment == 0). Covers all `cash-advance-payout-integrity` spec scenarios.
- [ ] 2.3 Create `CommandCenter.Tests/Integration/CashAdvanceEnvelopeTests.cs` — `TEST_POSTGRES_CONNECTION`-gated: verify the execution-strategy envelope commits both sale and drawer movements atomically; on sale failure, no drawer transaction persists. Covers spec: "Atomic Payout and Accounting Sale" scenarios.
- [ ] 2.4 Modify `Sales.Module/Services/CashDrawerService.cs` — remove `ProcessCashAdvanceAsync` (`:482-615`), remove `IServiceProvider? _serviceProvider` (`:17`), remove constructor param (`:22`), remove `GetSalesService()` (`:28-31`), remove `GetSettingsService()` (`:33-36`), remove `GetCommissionPercentageAsync` (`:38-53`). Constructor becomes `CashDrawerService(SalesDbContext context)`. Covers spec: "No Service Locator" scenario "Drawer has no locator".
- [ ] 2.5 Modify `Sales.Module/Interfaces/ICashDrawerService.cs` — remove `ProcessCashAdvanceAsync` signature; keep `CashAdvanceResultDto` class (used by coordinator).
- [ ] 2.6 Re-point `CommandCenter.Tests/CashAdvanceTests.cs` — update 9 `ProcessCashAdvanceAsync` call sites (lines ~71, 107, 142, 168, 190, 216, 247, 275, 299) to use `CashAdvanceCoordinator.ProcessAsync` instead of `CashDrawerService.ProcessCashAdvanceAsync`. Update `CreateCashDrawerServiceWithSalesService` helper to construct coordinator with proper DI.
- [ ] 2.7 Re-point `CommandCenter.Tests/Unit/CashDrawerServiceUnitTests.cs:87-166` — remove tests that construct `CashDrawerService` with provider and assert drawer-only effects; migrate relevant assertions to `CashAdvanceCoordinatorTests`. Re-point `CommandCenter.Tests/CashDrawerClosureTests.cs:259-264` if referencing the removed method.
- [ ] 2.8 Modify `Backend.API/Controllers/CashDrawerController.cs:309` — inject `CashAdvanceCoordinator`; replace `ProcessCashAdvanceAsync` call with coordinator's `ProcessAsync`.
- [ ] 2.9 Modify `Backend.API/Startup/ServiceCollectionExtensions.cs` — register `CashAdvanceCoordinator` as scoped.

## Phase 3: Classifier Swap (Slice 3)

- [ ] 3.1 Modify `Backend.API/Controllers/ShiftsController.cs:279-298` — replace local heuristic (`Contains("usd")`, `Contains("dolar")`, `Contains("$")`) with `PaymentMethodCurrencyResolver.Resolve(d.PaymentMethodName)`; replace inline `/ exchangeRate` division with `PricingCalculator.ToUSD(amount, exchangeRate)` (AD-7). Add `using Sales.Module;` and `using Core.Helpers;`. Covers spec: `payment-method-currency-classification` all 3 requirements.
- [ ] 3.2 Create `CommandCenter.Tests/Unit/PaymentMethodCurrencyClassificationTests.cs` — test scenarios: "Report uses the shared classifier" (method named "Dólares" → USD); "Bs.S total is converted with the shared helper" (value equals `PricingCalculator.ToUSD`); "Report matches the stored receipt" (label and amount agree with receipt/PDF classification).

## Phase 4: Verification

- [ ] 4.1 Run `dotnet build CommandCenter.slnx -c Release` — 0 errors, 0 warnings.
- [ ] 4.2 Run `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` — all tests pass.
- [ ] 4.3 Run coverage gate: `dotnet test --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings` → Sales.Module ≥ 0.80.
- [ ] 4.4 Verify `CashDrawerService.cs` is under 500 lines, has no `IServiceProvider`, no `ISalesService`.
