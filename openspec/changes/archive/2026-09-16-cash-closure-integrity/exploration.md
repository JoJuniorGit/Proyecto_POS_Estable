## Exploration: cash-closure-integrity (C1 — H-01 midnight arqueo gap + H-02 Service Locator)

Verified against HEAD `441d014`. Artifact store: openspec. Read-only on code.

### Current State

**Arqueo window (H-01).** `DailyClosureService.GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc)` (`Sales.Module/Services/DailyClosureService.cs:24-106`) rebuilds the expected totals from scratch on every call:

- `:27-31` derives the Venezuela business day of `dateUtc` and computes `startOfDayUtc` / `endOfDayUtc = startOfDayUtc.AddDays(1)`.
- `:34-42` loads the latest `DailyClosure` ordered by `ClosureDate` and computes
  `effectiveStartTime = (lastClosure != null && lastClosure.ClosureDate > startOfDayUtc) ? lastClosure.ClosureDate : startOfDayUtc`.
  The ternary **clamps to the calendar-day start** whenever the last closure is older than today's local midnight. Confirmed: night shift opened day 1 20:00, last closure day 1 23:00, closure at day 2 00:15 → `lastClosure(day1 23:00) > startOfDayUtc(day2 00:00)` is false → window starts at day 2 00:00 and **all sales/payments from day 1 23:00 to 23:59:59 are excluded** from the arqueo.
- `:45-53` (sales) and `:55-64` (physical cash change) both use strict operators `> effectiveStartTime` and `< endOfDayUtc`. The `>` drops any row whose timestamp is exactly the window start (notably a sale exactly at local midnight when the window collapsed to `startOfDayUtc`).
- `:124-201` `CreateClosureAsync` re-derives the same totals internally at `:138` via `GetExpectedTotalsByPaymentMethodAsync(closure.ClosureDate)` and persists them into `ClosureDetail.ExpectedAmountBsS`.
- Consumers: `Backend.API/Controllers/DailyClosureController.cs:142` (passes `closureDate` = `UtcNow` unless an Admin backdates within 24h, `:101-129`) and `Backend.API/Controllers/ShiftsController.cs:93` (passes `DateTime.UtcNow`). A third consumer is the preview endpoint `DailyClosureController.cs:54` (`[FromQuery] DateTime dateUtc`, **unvalidated** — an omitted query value becomes `0001-01-01`).
- No `CashDrawerSession` is consulted: the window is purely calendar + last-closure based, while the money physically lives in the drawer session created by `CashDrawerService.RolloverSessionAfterClosureAsync` (`CashDrawerService.cs:250-262`).

**Service Locator (H-02).** `Sales.Module/Services/CashDrawerService.cs` (616 lines, over the 300–500 core limit):

- `:17` `private readonly IServiceProvider? _serviceProvider;`, `:22-26` optional constructor parameter, `:28-31` `GetSalesService()`, `:33-36` `GetSettingsService()`.
- Real DI cycle: `SalesService` requires `ICashDrawerService` by constructor (`SalesService.cs:36-56`); `CashDrawerService` needs `ISalesService` only inside `ProcessCashAdvanceAsync` (`:534-559`).
- `:534-541` — when `GetSalesService()` returns null the code only calls `AppLogger.LogWarn` and **continues**: the physical cash expense is still paid out at `:562-572` while the accounting `Sale` is never created. This is the inventory + fiscal traceability hole.
- `:38-53` — the second locator silently falls back to 10% / 7% commissions when `ISystemSettingsService` is unavailable.
- `ISystemSettingsService` is **not** part of the cycle (it has no dependency on sales or drawer), so it can be constructor-injected today with zero structural risk.
- Registration is plain `AddScoped` (`Backend.API/Startup/ServiceCollectionExtensions.cs:52-53`); the container is never told about the deferred dependency, which is why tests construct `new CashDrawerService(context, provider)` by hand (`CommandCenter.Tests/CashAdvanceTests.cs:37-59, 240-244`).
- `SalesService.CreateCashAdvanceSaleAsync` lives in the partial `SalesService.CashAdvance.cs:12-155` and uses only `_context`, `_inventoryService`, the private `GenerateNextInvoiceNumberAsync()` (`SalesService.cs:78-94`) and the private `ResolveAnchoredRateAsync()` (`SalesService.Mapping.cs:104`). It does **not** touch `_cashDrawerService`, so the sale-creation capability is structurally independent of the drawer.

**Adjacent candidate — USD classification divergence.** `ShiftsController.GetReportById` (`:279-298`, reached by `GET /api/shifts/current/report` and `GET /api/shifts/{id}/report`) classifies currency with its own heuristic at `:281` (`Contains("usd")`, `Contains("dolar")`, `Contains("$")`) and divides by `exchangeRate` instead of `PricingHelper.ToUSD`. Meanwhile `Sales.Module/PaymentMethodCurrencyResolver.cs:20-29` (documented as "8.9-M16 única fuente de verdad") matches **only** the literal `USD` and is used by `DailyClosureService.cs:228,252`, `ClosurePdfGenerator.cs:72` and `PaymentMethod.Currency`. So `GetReportById` and the stored receipt/PDF can classify the same payment method differently (e.g. a method named "Dólares" or "Efectivo $"). `GetCurrentReport` has no test coverage within 3 caller hops.

**Controller orchestration duplication.** `DailyClosureController.CreateClosure` (`:85-189`) and `ShiftsController.CloseShift` (`:75-217`) implement the same serializable-transaction routine (duplicate check → Serializable tx → expected totals → detail mapping → `CreateClosureAsync` → `RolloverSessionAfterClosureAsync` → commit → `WriteClosedClosureReceipts`), but diverge in USD handling (`ShiftsController.cs:115-122, 183-185` divides/converts; `DailyClosureController` stays in Bs.S). This is audit H-10, planned for a later slice.

### Affected Areas

- `Sales.Module/Services/DailyClosureService.cs` — window computation (`:27-42`) and both boundary predicates (`:47-50`, `:55-64`); 368 lines, stays under the limit after adding a small extraction.
- `Sales.Module/Interfaces/IDailyClosureService.cs:17` — contract of the arqueo query (may gain an explicit window/as-of concept).
- `Backend.API/Controllers/DailyClosureController.cs:54-58, 142` — preview + closure callers; also the unvalidated `[FromQuery] DateTime dateUtc`.
- `Backend.API/Controllers/ShiftsController.cs:93` — current-shift caller; `:279-298` — USD classification divergence.
- `Sales.Module/Services/CashDrawerService.cs` — remove both locators (`:17, 22-36, 38-53`), rehome/settle `ProcessCashAdvanceAsync` (`:482-615`).
- `Sales.Module/Services/SalesService.cs:36-56` and `Sales.Module/Services/SalesService.CashAdvance.cs:12-155` — the other edge of the cycle and the sale-creation capability.
- `Sales.Module/Interfaces/ICashDrawerService.cs:75-83` — `ProcessCashAdvanceAsync` signature / `CashAdvanceResultDto`.
- `Backend.API/Controllers/CashDrawerController.cs:290-325` — the only production caller of `ProcessCashAdvanceAsync`.
- `Backend.API/Startup/ServiceCollectionExtensions.cs:51-57, 69-73` — registrations; MediatR already registered from the `Sales.Module` and `Inventory.Module` assemblies.
- `Sales.Module/PaymentMethodCurrencyResolver.cs` — must become the single classifier on the report path.
- Tests: `CommandCenter.Tests/Unit/DailyClosureServiceUnitTests.cs`, `CommandCenter.Tests/CheckoutAndPaymentTests.cs:317-420`, `CommandCenter.Tests/Integration/DailyClosureFlowIntegrationTests.cs`, `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs` (Postgres-gated), `CommandCenter.Tests/CashAdvanceTests.cs`, `CommandCenter.Tests/Unit/CashDrawerServiceUnitTests.cs:87-166`, `CommandCenter.Tests/CashDrawerClosureTests.cs:142-264`, `CommandCenter.Tests/Unit/CashDrawerRbacAndPaymentMethodsTests.cs`.

### Approaches

#### H-01 — arqueo window

1. **Session-anchored window (recommended)** — resolve the window start from the physical drawer: `activeSession.OpenedAt ?? lastClosure.ClosureDate ?? startOfDayUtc` (no calendar clamp), keep the right bound `endOfDayUtc` derived from the query day, and change both predicates to `>= effectiveStartTime && < endOfDayUtc`. Extract the window math into a small pure helper so it is unit-testable without a `DbContext`.
   - Pros: removes the midnight gap at its root; also bounds the opposite failure (a shift never closed for days would otherwise pull in every sale back to the first session); arqueo now matches the drawer session that physically holds the cash, which is exactly the reconciliation formula in the financial-integrity skill; fix lands in the shared service so **both** controllers inherit it with no consumer change; no schema/migration.
   - Cons: `DailyClosureService` gains a read of `CashDrawerSessions` (new coupling to the drawer domain, same `SalesDbContext`); needs an explicit precedence rule when an Admin backdates a closure behind the current session (see Risks); previews for a past date may report different expected amounts than before the fix.
   - Effort: Medium.

2. **Last-closure only, drop the clamp** — `effectiveStartTime = lastClosure?.ClosureDate ?? startOfDayUtc`, keep `endOfDayUtc`, fix the operators.
   - Pros: ~3-line change, no new coupling, directly deletes the reported clamp, preserves all existing tests.
   - Cons: if no closure exists within the lookback (POS left open, closure failed), the window silently spans multiple days and over-counts expected cash; needs a separate max-lookback guard that is its own design decision; does not anchor to the physical session.
   - Effort: Low.

3. **Explicit window parameter / `ClosureWindow` value object** — callers (or a coordinator) pass `(fromUtc, toUtc)`; the current method becomes a thin default wrapper.
   - Pros: window policy becomes explicit and independently testable; gives the later H-10 controller dedup a clean seam.
   - Cons: pushes window policy into the two controllers now — recreating the very duplication H-10 will remove and risking divergence in this slice; larger review surface.
   - Effort: Medium.

#### H-02 — removing the Service Locator without a DI cycle

1. **`CashAdvanceCoordinator` application service (recommended)** — a new scoped class owning the cash-advance orchestration: validates the amount, resolves the commission from a constructor-injected `ISystemSettingsService`, opens the cross-DB transaction/execution strategy, calls `ISalesService.CreateCashAdvanceSaleAsync(..., existingTransaction)`, then calls `ICashDrawerService.AddTransactionAsync` for the physical expense and the commission income. `CashDrawerService` drops `ProcessCashAdvanceAsync` and both locators; `CashDrawerController:309` injects the coordinator.
   - Pros: mirrors the audit recommendation and the existing `ServiceRestartCoordinator` precedent; no cycle because the coordinator is a third node (`coordinator → ISalesService → ICashDrawerService`, with the `ISalesService` edge removed from the drawer); keeps the anchored-rate-then-cash-movements ordering explicit and testable; shrinks `CashDrawerService` back under the 500-line rule.
   - Cons: moves ~130 lines and the transaction envelope into a new class; `CashAdvanceTests`, `CashDrawerServiceUnitTests:87-166`, `CashAdvanceTests` and `CashDrawerClosureTests:264` must be re-pointed at the coordinator (they currently instantiate `CashDrawerService` directly, so this is mechanical but real churn).
   - Effort: Medium.

2. **MediatR `CreateCashAdvanceSaleCommand`** — `CashDrawerService` injects `IMediator`; a handler in `Sales.Module` (already scanned at `ServiceCollectionExtensions.cs:71`) resolves `SalesService` and creates the sale in the ambient scope/transaction.
   - Pros: genuinely breaks the constructor cycle (the handler is resolved after `CashDrawerService` is fully constructed); MediatR is already a codebase dependency.
   - Cons: the codebase uses MediatR for **domain notifications only** (`SaleMadeEvent`, `SaleDispatchedEvent`), never for service-to-service command dispatch, so this introduces a new convention; it hides a synchronous, transaction-critical dependency behind the mediator; correctness depends on the handler resolving the same scoped `SalesDbContext`/transaction, which is subtle and hard to assert in tests; the response has to travel back through `Send<TResponse>` to populate `RelatedSaleId`/`InvoiceNumber`.
   - Effort: Medium-High.

3. **Deferred `Func<ISalesService>` + fail-fast (minimal)** — register `builder.Services.AddScoped<Func<ISalesService>>(sp => () => sp.GetRequiredService<ISalesService>())`, inject that delegate instead of `IServiceProvider`, and replace the null branch with a throw so cash can never leave the drawer without its accounting sale.
   - Pros: smallest diff that removes the raw locator, the `null!` construction seam and the silent skip; no consumer or test-signature change; no new class.
   - Cons: still deferred/implicit resolution — a softer Service Locator, not a clean injected dependency; the cycle remains structurally hidden rather than eliminated; doesn't reduce `CashDrawerService` size.
   - Effort: Low.

### Recommendation

**H-01:** Approach 1 (session-anchored window) with the window math extracted into a small pure helper (e.g. a `ClosureWindow` resolver returning a shown-by-value `(StartUtc, EndExclusiveUtc)`), implementing `>= start && < end`. It is the only option that fixes both the reported midnight gap and the unbounded "never closed" case while matching the drawer session that actually holds the money — and because both controllers share the service method, the fix propagates without touching either controller. Keep `endOfDayUtc` as the exclusive right bound in this slice to preserve parity with already-persisted `ClosureDetail.ExpectedAmountBsS` values and the existing tests; revisit "as-of" semantics only if exact preview/stored parity is later required.

**H-02:** Approach 1 (`CashAdvanceCoordinator`), combined with two non-negotiables that apply to every option: (a) `ISystemSettingsService` becomes a normal constructor dependency (it is not part of the cycle), and (b) the "service unavailable" branch becomes a hard failure — the physical payout must never succeed without its accounting sale, so the null path must not exist at all once the dependency is injected. Approach 3 is a legitimate size-reduction fallback if the 400-line budget is tight, but it leaves the structural defect in place.

**Adjacent candidates:** include the `ShiftsController.GetReportById` classifier swap to `PaymentMethodCurrencyResolver` (+ `PricingHelper.ToUSD` for the division) in this slice — it is ~6 lines, sits on the same arqueo/report surface, and removes an active contradiction with the documented "single source of truth" (a rename of a payment method currently distorts Z-report labels/amounts differently in the report than in the stored receipt/PDF). **Defer** the H-10 controller-orchestration consolidation to its own slice: it rewrites the exact lines H-01 touches and would blow the review budget; the H-01 fix already reaches both controllers through the shared service, so deferring costs nothing.

### Risks

- **Backdated-closure precedence (unresolved, needs a design decision).** An Admin may register a closure up to 24h in the past (`DailyClosureController.cs:112-123`). If the session start wins over `lastClosure.ClosureDate`, a backdated closure whose window falls before the current session's `OpenedAt` can produce an empty or wrong window. A guard is required (e.g. fall back to `startOfDayUtc` when the resolved start is not `< endOfDayUtc`, and/or refuse to mix a session-anchored window with a backdated date).
- **Behavior change in previews.** Operators will see different expected totals for night shifts after the fix, and a past-date preview may no longer reproduce the persisted closure. Persisted history must not be rewritten (snapshot immutability); this is a display/expectation change that deserves a release note.
- **Boundary operator semantics.** Switching `>` to `>=` includes a row timestamped exactly at the window start. With a session-anchored start (the session `OpenedAt`, not a sale timestamp) this is safe, but the right bound must stay exclusive to avoid the next-midnight sale leaking into the wrong day.
- **Financial snapshots must not move.** Any H-02 relocation must preserve the exact current ordering (accounting sale first → its `AppliedRate` anchors the drawer movements), the persisted fields `AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS` (`RoundingAdjustment` stays at its default 0), `Math.Round(..., 4, AwayFromZero)` for USD and `Math.Round(..., 2, AwayFromZero)` for Bs.S, and must not recompute any historical closure.
- **Commission fallback is money-affecting.** Once `ISystemSettingsService` is injected, the 10%/7% fallback becomes reachable only on a genuinely missing/invalid setting; decide whether that must stay silent, be audited, or fail closed before checkout.
- **Test coverage gaps that must be closed by this change.** (1) No test crosses local midnight — the H-01 regression is invisible today. (2) `CashAdvanceTests.cs:65,138,164,211,292` and `CashDrawerServiceUnitTests.cs:88-166` build `CashDrawerService` **without** a provider and assert only drawer effects — i.e. the silent-skip is currently tolerated by the suite; the new invariant ("no accounting sale ⇒ no payout") needs a dedicated failing-first test. (3) `GetCurrentReport`/`GetReportById` has no test in 3 hops. (4) `DailyClosureController.GetExpectedTotals` accepts an unvalidated `dateUtc`.
- **Test infrastructure limits.** Unit/integration closure tests run on EF InMemory (`TestDatabaseFactory.cs:14-22`), where transactions and advisory locks are ignored; the Serializable cross-DB behavior is only covered by the `TEST_POSTGRES_CONNECTION`-gated `DailyClosureRetryIntegrationTests` and `DailyClosureFlowIntegrationTests` are InMemory-only. Window semantics can be unit-tested purely, but any assertion about transactional envelope behavior must go to the Postgres-gated suite.
- **`ShiftsController` uses `DateTime.UtcNow` while `DailyClosureController` uses `closureDate`.** Harmless today, but it becomes a divergence the moment the window start is no longer clamped by the calendar day; worth pinning down in the design even if H-10 is deferred.

### Ready for Proposal

Yes. Scope for C1: (1) session-anchored, half-open arqueo window with extracted pure window resolver + `>=`/`<` boundary fix, (2) `CashAdvanceCoordinator` removing both Service Locators, with `ISystemSettingsService` constructor-injected and the payout/accounting-sale coupling made atomic and non-silent, (3) the ~6-line `PaymentMethodCurrencyResolver` unification in `ShiftsController.GetReportById`. Explicitly out of scope: H-10 controller orchestration dedup, any schema/migration change, and any recomputation of persisted closures. Two decisions the orchestrator should surface to the user before design: session-vs-last-closure precedence for backdated closures, and whether an invalid/missing commission setting must fail closed.
