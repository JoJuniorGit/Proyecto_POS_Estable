# Design: cash-closure-integrity

## Technical Approach

Three self-verifiable slices. (1) Arqueo window de-calendarized: a pure `ClosureWindowResolver` returns `(StartUtc, EndExclusiveUtc)`; `DailyClosureService` feeds it the active drawer session and switches both predicates to `>= start && < end`. (2) Cash-advance orchestration moves into `CashAdvanceCoordinator`, which owns the sale-then-drawer envelope and deletes both Service Locators. (3) `GetReportById` adopts the shared classifier. No schema change, no snapshot rewrite.

## Architecture Decisions

| # | Decision | Choice | Rejected | Rationale |
|---|----------|--------|----------|-----------|
| AD-1 | Resolver | Static `ClosureWindowResolver` in `Sales.Module/Services/`, returns `(DateTime StartUtc, DateTime EndExclusiveUtc)` | Value object | Tuple = ~3-line diff; no new type to build in tests |
| AD-2 | Session source | Read `CashDrawerSessions` (`Status == Open`) off the existing scoped `SalesDbContext` | Inject `ICashDrawerService` | 12 test/site call sites use `new DailyClosureService(context)`; a second ctor param churns them for zero gain |
| AD-3 | Backdated guard | If the anchor `>= EndExclusiveUtc`, re-apply the legacy clamped rule bounded to the day: `(lastClosure > startOfDay && lastClosure < endOfDay) ? lastClosure : startOfDayUtc` | Trust the anchor; allow an empty window | That ternary *is* the spec's "last-closure/start-of-day rule"; bounding it makes `StartUtc < EndExclusiveUtc` true by construction |
| AD-4 | Envelope owner | Coordinator injects `SalesDbContext` + `ISalesService` + `ICashDrawerService` + `ISystemSettingsService` | A transaction method on `ICashDrawerService` | `BeginTransactionAsync` needs the unit of work; leaving it in the drawer re-creates H-02. **Deviation from the brief** |
| AD-5 | Logging | `AppLogger.LogWarn` + throw; no `ILogger<>` | `ILogger<CashAdvanceCoordinator>` | The only log here was the deleted fail-open branch; `SalesService` is the sole `Sales.Module` service taking an optional nullable `ILogger<T>`. **Deviation from the brief** |
| AD-6 | Fail-closed error | `InvalidOperationException` from `Sales.Module` | New domain exception | The middleware maps it to 409 and its "Sales" prefix check surfaces the message — as for insufficient cash; no H-11 scope |
| AD-7 | USD helper | `PricingCalculator.ToUSD(amount, rate)`, 2 decimals | `PricingHelper.ToUSD` | **Spec conflict:** `PricingHelper` is unreferenced by `Backend.API.csproj`; adding it inverts the layering. It delegates to `PricingCalculator.ToUSD`, so tests still assert the scenario via `PricingHelper` |
| AD-8 | Preview `dateUtc` | Validate it (`== default` → 400) | Residual risk | The only caller always sends `dateUtc={dateUtc:O}`; the Web client never calls it |

## Data Flow

    GetExpectedTotalsByPaymentMethodAsync
      TimeZoneHelper → startOfDayUtc;  Sessions(Open)?.OpenedAt;  last ClosureDate
        → ClosureWindowResolver → (Start, End) → sales/change rows: >= Start && < End

    POST /cashdrawer/cash-advance → CashAdvanceCoordinator
      validate amount+balance (outside envelope) ▸ commission → CreateCashAdvanceSaleAsync(existingTx)
      → AddTransactionAsync ×2 → commit

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Sales.Module/Services/ClosureWindowResolver.cs` | Create | Pure resolver (AD-1, AD-3) |
| `Sales.Module/Services/DailyClosureService.cs` | Modify | Session read, resolver call, predicates (`:34-64`) |
| `Sales.Module/Services/CashAdvanceCoordinator.cs` | Create | Advance orchestration + envelope |
| `Sales.Module/Services/CashDrawerService.cs` | Modify | Drop `ProcessCashAdvanceAsync` (`:482-615`) and locators (`:17, 22-53`); ~455 lines |
| `Sales.Module/Interfaces/ICashDrawerService.cs` | Modify | Drop `ProcessCashAdvanceAsync`; `CashAdvanceResultDto` stays |
| `Backend.API/Controllers/CashDrawerController.cs` | Modify | Inject coordinator (`:309`) |
| `Backend.API/Controllers/{Shifts,DailyClosure}Controller.cs` | Modify | Classifier swap (`:279-298`); validate `dateUtc` (`:54`) |
| `Backend.API/Startup/ServiceCollectionExtensions.cs` | Modify | Register coordinator |

## Interfaces / Contracts

```csharp
public static (DateTime StartUtc, DateTime EndExclusiveUtc) ClosureWindowResolver.Resolve(
    DateTime dateUtc, DateTime? activeSessionOpenedAtUtc, DateTime? lastClosureDateUtc);

public Task<CashAdvanceResultDto> CashAdvanceCoordinator.ProcessAsync(int sessionId, decimal requestedAmountLocal,
    int paymentMethodId, string paymentMethodName, bool isTransfer, decimal exchangeRate,
    int? cashierId = null, string? userName = null);
```

Classifier swap replacing `ShiftsController.cs:281-284`; the output DTO is unchanged:

```csharp
string currency = PaymentMethodCurrencyResolver.Resolve(d.PaymentMethodName);
bool isUsd = currency == PaymentMethodCurrencyResolver.Usd;
decimal systemAmt = isUsd ? PricingCalculator.ToUSD(d.ExpectedAmountBsS, exchangeRate) : d.ExpectedAmountBsS;
decimal declaredAmt = isUsd ? PricingCalculator.ToUSD(d.ActualAmountBsS, exchangeRate) : d.ActualAmountBsS;
```

`ShiftsController` needs `using Sales.Module;` and `using Core.Helpers;`. DI stays acyclic: `coordinator → ISalesService → ICashDrawerService → SalesDbContext`.

## Testing Strategy

| Layer | What to test | Approach |
|-------|--------------|----------|
| Unit | Resolver: 20:00→00:15 crossing, rows at either bound, session-absent fallbacks, backdated guard | New `Unit/ClosureWindowResolverTests`, pure |
| Unit | Window end-to-end on EF InMemory; persisted `ClosureDetail` never rewritten | `DailyClosureServiceUnitTests`, `CheckoutAndPaymentTests:317-420` |
| Unit | Coordinator: configured commission; missing commission rejects with no payout/sale; `AppliedRate` anchors drawer movements; `RoundingAdjustment == 0` | New `Unit/CashAdvanceCoordinatorTests` |
| Unit | Report/receipt agreement: label from the resolver, amount equal to `PricingHelper.ToUSD` | New `CommandCenter.Tests` test |
| Integration | Serializable envelope behavior | `TEST_POSTGRES_CONNECTION`-gated `DailyClosureRetryIntegrationTests`; InMemory ignores transactions, so envelope assertions MUST NOT live there |

Re-pointed: `CashAdvanceTests` (`:58, 65, 138, 164, 211, 244, 272, 292`), `Unit/CashDrawerServiceUnitTests.cs:87-166`, `CashDrawerClosureTests.cs:259-264`, `Phase3ConcurrencyAndReservationTests.cs:103,124`. `ProcessCashAdvance_Cash_FallsBackTo10Percent_WhenSettingsMissing` inverts into the fail-closed test. `DailyClosureServiceUnitTests` keeps its signature (AD-2). Gate: `Sales.Module ≥ 0.80`.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or process-integration boundary.

## Migration / Rollout

No migration, no data repair. Chained slices: **1** H-01 + resolver + preview validation (~200 lines); **2** H-02 coordinator (~300); **3** classifier (~30). Each verifies and reverts alone. Release note: night-shift previews report different expected totals; stored closures are never recomputed.

## Open Questions

- [ ] None blocking. Residual (accepted): `PricingCalculator.ToUSD` rounds report amounts to 2 decimals where inline division did not — bounded to ±0.005, inside the 0.05 tolerance.
