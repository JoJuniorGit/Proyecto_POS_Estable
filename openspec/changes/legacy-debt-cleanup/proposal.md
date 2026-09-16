# Proposal: legacy-debt-cleanup

## Intent

C1 closed H-01/H-02 but left 41 pre-existing debts registered in `docs/deuda-legacy-gga-2026-09-16.md` (GGA review of ANEXOS 8.137–8.139) plus the overlapping C2/C3 findings of `docs/analisis-exhaustivo-sistema-2026.md`. Highest risk: `ShiftsController.CloseShift` (`:117`, `:134`, `:185-187`) trusts `request.Currency` as a second source of truth beside `PaymentMethodCurrencyResolver` — a client can alter the arqueo (item 26 / 8.139-G2). Five sites also return anonymous error objects instead of the repo's RFC 7807 helpers.

## Scope

### In Scope

| Group | Items | Finding |
|---|---|---|
| A EF entities across the API boundary | 3, 8, 15, 19, 20 | H-04 |
| B CancellationToken sweep | 4, 9, 16, 21, 27 | H-09 |
| C RFC 7807 via `ApiProblemResults` | 2, 18, 25 | H-11 |
| D Explanatory comments / artifact language | 5, 10, 17, 23, 24, 33, 38 | — |
| E EF reads (`AsNoTracking`/`AsSplitQuery`) | 7, 22, 31 | — |
| F SRP / McCabe | 6, 13, 14 | H-10 |
| G Zero-trust close (server-side resolver) | 26, 39 | H-10 |
| H Guards, dead contract, dead code, naming | 11, 12, 28, 29, 32, 34, 35, 36, 37 | — |
| I `DbContext` out of controllers | 1, 30 | H-03 |
| J C2/C3 findings with no registry item | — | H-05 (LAN cookie), H-06 (WPF `IDisposable`), H-08 (client pagination), H-14 (`async void OnClosing`) |

### Out of Scope

C4: H-07, H-12, H-13. Everything closed by C1 (H-01/H-02, currency classifier). Registry items 40 (OpenCode `question` tooling) and 41 (WPF hardcoded advance commission), deferred ungrouped — 41 is the top follow-up candidate.

## Capabilities

### New Capabilities

- `api-error-contract`: touched endpoints return RFC 7807 payloads via `ApiProblemResults`; no anonymous error objects or `message`/`Message` casing drift.
- `api-dto-boundary`: no EF entity crosses the API boundary for drawer/closure contracts.
- `closure-orchestration-consolidation`: one server-side closure path; controllers authorize and delegate only.
- `async-cancellation-propagation`: touched async endpoints and services accept and propagate `CancellationToken`.

### Modified Capabilities

- `payment-method-currency-classification`: `CloseShift` MUST classify every declared method server-side via `PaymentMethodCurrencyResolver`, and `request.Currency` MUST NOT influence amounts, status, or the stored closure.

Groups D/E/H add no spec-level requirements (behavior-preserving hygiene), except item 12 (logging replaces silent `catch`) and item 35 (removal of discarded `CloseShiftRequest` fields) — a real contract change covered under `api-error-contract`.

## Approach

Priority-ordered, blocker-first cleanup in five candidate slices, coherent by domain/file; fine sizing deferred to tasks. Delivery is ask-on-risk.

1. **S1 Zero-trust close** (G): items 26, 39. `ShiftsController.cs`, `Web.Frontend/src/pages/RegisterClosePage.jsx`. Blocker.
2. **S2 Error contract** (C): items 2, 18, 25 recomposed onto `ApiProblemResults`. Three controllers.
3. **S3 Closure layering + dedup** (I, F): items 1, 6, 13, 14, 30. `DbContext` and orchestration move into `IDailyClosureService`; `CreateClosure` complexity falls under 10.
4. **S4 DTO boundary** (A): items 3, 8, 15, 19, 20. New immutable drawer/closure DTOs replace entity returns.
5. **S5 Hardening sweep** (B, E, H, D, J): CT propagation, EF read tuning, guards/dead code/naming, comments/language, H-05/H-06/H-08/H-14.

Each slice builds, tests, verifies and reverts alone; slices land chained against the 400-line review budget. No schema change anywhere.

## Affected Areas

| Area | Impact |
|------|--------|
| `Backend.API/Controllers/{Shifts,CashDrawer,DailyClosure,Auth}Controller.cs`, `Startup/ServiceCollectionExtensions.cs` | Modified |
| `Sales.Module/Services/{DailyClosureService,CashDrawerService}.cs`, `Interfaces/ICashDrawerService.cs` | Modified |
| Drawer/closure DTOs (`Sales.Module/Dtos/`) | New |
| `Desktop.Client.Core/ViewModels/*.cs`, `Desktop.Client/MainWindow.xaml.cs` | Modified |
| `Web.Frontend/src/pages/{RegisterPage,RegisterClosePage}.jsx` | Modified |
| `CommandCenter.Tests/**` (re-pointed + new tests) | Modified |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Server-side classification flips a stored shift's arqueo status | Med | report↔receipt agreement test per closure (C1 P1 pattern); snapshots never rewritten |
| DTO swap breaks WPF/Web bindings (items 3, 8, 15, 19, 20) | High | contract freeze; both clients updated in the same slice; run both suites |
| Removing discarded `CashierName`/`CashierCedula` breaks a sender (item 35) | Med | grep client senders first; keep optional if any sender exists |
| Coverage gate drops as dead code/tests are deleted | Med | re-point tests in the same commit; measure before deleting |
| 39+ items blow the 400-line review budget | High | chained slices, ask-on-risk |
| Group J widens scope to WPF/Web beyond the backend-first mandate | Med | maintainer may defer J to a C3b follow-up |

## Rollback Plan

No migrations or data repair. Revert each slice's commits independently. S1 and S4 pair a server change with its client callers (Web `shiftApi.js`/`RegisterClosePage.jsx`, WPF view models), so those two revert server and client together. Snapshots stay authoritative; no closure is ever recomputed.

## Dependencies

None added. `ApiProblemResults` and `PaymentMethodCurrencyResolver` already exist. Postgres (`TEST_POSTGRES_CONNECTION`) only for the pre-existing gated integration tests.

## Success Criteria

- [ ] `dotnet build CommandCenter.slnx -c Release`: 0 errors, 0 warnings (`TreatWarningsAsErrors`).
- [ ] `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`: 100% passing.
- [ ] `npm test` and `npm run lint` in `Web.Frontend`: 100% passing, lint clean.
- [ ] Coverage maintained: Core ≥ 0.70, Sales ≥ 0.80, Inventory ≥ 0.72.
- [ ] Zero `request.Currency` reads in `CloseShift`; the resolver classifies every declared method (item 26).
- [ ] No `DbContext` injected in touched controllers; no EF entity in touched API responses.
- [ ] Every closed registry item cites file:line plus test evidence in the verify report; items 40/41 listed as deferred.

**Review workload**: far over the 400-line budget — chained slices are mandatory, sized in the tasks phase.
