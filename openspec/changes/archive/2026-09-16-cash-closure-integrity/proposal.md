# Proposal: cash-closure-integrity

## Intent

Night-shift arqueo silently drops sales between local midnight and the closure (H-01), under-counting expected cash; a cash advance can pay physical cash while its accounting sale is silently skipped and the commission silently falls back to 10%/7% (H-02). Both break drawer reconciliation.

## Scope

### In Scope

- **H-01** — session-anchored half-open window `activeSession.OpenedAt ?? lastClosure.ClosureDate ?? startOfDayUtc` with `>= start && < endOfDayUtc`; the math moves to a pure, `DbContext`-free resolver; empty backdated windows fall back to lastClosure/start-of-day only there.
- **H-02** — `CashAdvanceCoordinator` (scoped) owns advance orchestration; `CashDrawerService` drops `ProcessCashAdvanceAsync` and both Service Locators; `ISystemSettingsService` is constructor-injected; an unresolvable commission fails closed; payout and accounting sale atomic and non-silent.
- **Adjacent** (~6 lines): currency classifier swap in `GetReportById`.

### Out of Scope

H-10 controller-orchestration dedup; schema/migrations; recomputation of persisted closures.

## Capabilities

### New Capabilities

- `cash-closure-arqueo-window`: arqueo window anchoring.
- `cash-advance-payout-integrity`: advance orchestration and commission.
- `payment-method-currency-classification`: USD/Bs.S classifier source of truth.

### Modified Capabilities

None — no existing spec covers closures, drawer, or currency; the four current specs stay untouched.

## Approach

A pure `ClosureWindowResolver` returns `(StartUtc, EndExclusiveUtc)`; `DailyClosureService` consumes it and switches both predicates, so both controllers inherit it unchanged. A third graph node — `coordinator → ISalesService → ICashDrawerService` — drops the `ISalesService` edge from the drawer (mirrors `ServiceRestartCoordinator`), preserving ordering (sale first, rate-anchored) and snapshots (`AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS`; `RoundingAdjustment` stays 0).

## Affected Areas

| Area | Impact |
|------|--------|
| `Sales.Module/Services/DailyClosureService.cs` | Modified |
| `Sales.Module/Services/ClosureWindowResolver.cs` | New |
| `Sales.Module/Services/CashDrawerService.cs` | Modified |
| `Sales.Module/Services/CashAdvanceCoordinator.cs` | New |
| `Backend.API/Controllers/{CashDrawer,Shifts}Controller.cs`, `Startup/ServiceCollectionExtensions.cs` | Modified |

## Verification

`CommandCenter.Tests`: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`. Build: `dotnet build CommandCenter.slnx -c Release`. Closure units run on EF InMemory; Postgres-gated tests need `TEST_POSTGRES_CONNECTION`.

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Backdated closure yields empty/wrong window | Med | explicit fallback branch, unit-tested |
| Night-shift preview totals change | High | release note; snapshots untouched |
| Row exactly at window start included | Low | start is session `OpenedAt`; right bound exclusive |

## Rollback Plan

No schema change: revert the branch and redeploy the previous build. Snapshots stay authoritative; no data repair needed.

## Dependencies

None (`ISystemSettingsService`, MediatR already registered).

## Success Criteria

- [ ] A closure at 00:15 includes sales from the prior 20:00–23:59:59 session.
- [ ] No payout without its accounting sale; a missing commission rejects the advance.
- [ ] `CashDrawerService` under 500 lines, no `IServiceProvider`.
- [ ] Report and stored receipt agree on payment-method currency; build clean; new boundary tests pass.

**Review workload**: ~500–650 changed lines, over budget — chained slices recommended (1: H-01; 2: H-02; 3: classifier).
