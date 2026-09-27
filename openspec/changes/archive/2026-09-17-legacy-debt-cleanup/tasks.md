# Tasks: legacy-debt-cleanup

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1770 (S1 ~150, S2 ~120, S3 ~350, S4a ~250, S4b ~300, S5a ~150, S5b ~250, S5c ~200) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | 8 PRs: S1 → S2 → S3 → S4a → S4b → S5a → S5b → S5c |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: Yes
Chained PRs recommended: Yes
Chain strategy: pending
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| S1 | Zero-trust close — server resolver + Web page | PR 1 (~150 lines) | `dotnet test --filter "CloseShift"` | Close shift with mismatched currency; verify arqueo uses resolver | `ShiftsController.cs`, `DailyClosureService.cs`, `ShiftReportMapper.cs`, `RegisterClosePage.jsx`, `shiftApi.js` |
| S2 | Error contract + dead fields | PR 2 (~120 lines) | `dotnet test --filter "Error|Problem"` | POST close with invalid role → 403 RFC 7807; POST legacy fields → 200 | `ShiftsController.cs`, `CashDrawerController.cs`, `DailyClosureController.cs`, `shiftApi.js` |
| S3 | Closure orchestration consolidation | PR 3 (~350 lines) | `dotnet test --filter "Closure|DailyClosure"` | Create closure twice → same amounts/status; preview 400 preserved | `IDailyClosureService.cs`, `DailyClosureService.cs`, controllers, `ServiceCollectionExtensions.cs` |
| S4a | Closure DTO boundary | PR 4 (~250 lines) | `dotnet test --filter "ClosureDto"` | GET closure → DTO fields match entity JSON names | `DailyClosureResponseDto.cs`, `ClosureDetailResponseDto.cs`, `ShiftReportMapper.cs`, client services |
| S4b | Drawer DTO boundary | PR 5 (~300 lines) | `dotnet test --filter "DrawerDto"` | Open session → DTO response; add transaction → DTO result | `CashDrawerSessionResponseDto.cs`, `CashTransactionResponseDto.cs`, `CashDrawerService.cs`, client views |
| S5a | CancellationToken propagation | PR 6 (~150 lines) | `dotnet test --filter "Cancellation"` | Cancel token mid-query → OperationCancelledException; no persistence | Touched controllers + services |
| S5b | EF tuning + guards/naming/comments | PR 7 (~250 lines) | `dotnet test --filter "Tuning|Guard"` | Read queries use AsNoTracking; null-guard throws ArgumentNullException | Read paths in services, `ClosureStatus.cs` |
| S5c | J findings (H-05/H-06/H-08/H-14) | PR 8 (~200 lines) | `dotnet test` + `npm test` | WPF close → no async void; cookie Secure flag set | `MainWindow.xaml.cs`, 5 VMs, `AuthController.cs`, `RegisterPage.jsx` |

## Phase 1: Zero-Trust Close (S1) — Blocker

- [x] 1.1 **RED**: Add test asserting `CloseShift` classifies every declared method via `PaymentMethodCurrencyResolver`, ignores `request.Currency`; unknown method id → 400
- [x] 1.2 Create `Sales.Module/Services/ShiftReportMapper.cs` — extract per-detail projection from `GetReportById` into reusable mapper (AD-4)
- [x] 1.3 Modify `Sales.Module/Services/DailyClosureService.cs`: accept `CreateClosureCommand` (AD-5), classify each `DeclaredPaymentAmount` via resolver (AD-1/3), `ExpectedAmountBsS` verbatim, `ActualAmountBsS = declaredNative × rate` (AD-3)
- [x] 1.4 Create `Sales.Module/Interfaces/CreateClosureCommand.cs` and `DeclaredPaymentAmount.cs` (AD-5)
- [x] 1.5 Modify `Sales.Module/Interfaces/IDailyClosureService.cs`: add `CreateClosureAsync(CreateClosureCommand, ct)` signature (AD-5)
- [x] 1.6 Modify `Backend.API/Controllers/ShiftsController.cs`: delete `request.Currency` reads (lines 117,134,185-187), build `CreateClosureCommand` from declarations, delegate to service (AD-1/5)
- [x] 1.7 Modify `Web.Frontend/src/pages/RegisterClosePage.jsx`: replace name/substring heuristic with `m?.currency === 'USD' ? 'USD' : 'Bs.S'` from server; stop sending `currency` in payload (AD-2)
- [x] 1.8 **GREEN**: Add test asserting report↔receipt agreement (C1 P1 pattern): close once, verify `GET /api/shifts/{id}/report` labels and amounts equal generated receipt per method
- [x] 1.9 **GREEN**: Add Web test asserting no `usd|dolar|$|divisa` substring heuristic remains in `RegisterClosePage.jsx`; payload shape has no `currency` key
- [x] 1.10 Re-point existing `CloseShift` test classes to use `CreateClosureCommand` without `Currency`

## Phase 2: Error Contract + Dead Fields (S2)

- [x] 2.1 **RED**: Add test asserting every error site in `ShiftsController`, `CashDrawerController.AddTransaction`, `DailyClosureController` returns RFC 7807 ProblemDetails with `status` matching HTTP status
- [x] 2.2 Modify `Backend.API/Controllers/ShiftsController.cs`: replace anonymous error objects at lines 74,106,239,266,278 with `ApiBadRequest`/`ApiForbidden`/`ApiNotFound` (AD-9)
- [x] 2.3 Modify `Backend.API/Controllers/CashDrawerController.cs`: replace anonymous error objects at lines 157,167,173,178 with `ApiProblemResults` helpers (AD-9)
- [x] 2.4 Modify `Backend.API/Controllers/DailyClosureController.cs`: replace legacy error sites with `ApiProblemResults` helpers; leave preview 400 on `Problem(...)` per REQ-AEC-01 (AD-9)
- [x] 2.5 Modify `Sales.Module/Services/DailyClosureService.cs`: async receipt writers — log failures via `AppLogger.LogWarn` (path + exception), replace `Thread.Sleep` with `await Task.Delay(200, ct)` (AD-10)
- [x] 2.6 Delete `CloseShiftRequest.CashierName` and `CashierCedula` (AD-11)
- [x] 2.7 Modify `Web.Frontend/src/services/shiftApi.js`: stop sending `cashierName`/`cashierCedula` (lines 10-13) (AD-11)
- [x] 2.8 **GREEN**: Add test asserting no anonymous error object remains in touched endpoints (code inspection or reflection)
- [x] 2.9 **GREEN**: Add test asserting legacy sender posting `cashierName`/`cashierCedula` still returns 200 (extra members ignored)
- [x] 2.10 **GREEN**: Add test capturing `AppLogger` output on forced receipt write failure — verify log entry with path + exception

## Phase 3: Closure Orchestration Consolidation (S3)

- [x] 3.1 **RED**: Add test asserting `DailyClosureController.CreateClosure` delegates to `IDailyClosureService` and persists nothing directly
- [x] 3.2 Create `Core/Interfaces/ITodayExchangeRateProvider.cs` (AD-6)
- [x] 3.3 Create `Backend.API/Services/TodayExchangeRateProvider.cs` — delegates to `ExchangeRateResolver.ReadEffectiveTodayRateAsync` (AD-6)
- [x] 3.4 Modify `Sales.Module/Services/DailyClosureService.cs`: extract `TryResolveBackdatedClosureDate`, `ValidateDeclaredMethods`, `MergeMissingMethods`/`RecalculateTotals` from `ExecuteClosureCoreAsync` (AD-8); inject `ITodayExchangeRateProvider` (AD-6); own `Serializable` transaction + persistence + rollover (AD-5)
- [x] 3.5 Modify `Backend.API/Controllers/DailyClosureController.cs`: remove rate resolution, transaction, persistence — delegate to service (AD-5/7)
- [x] 3.6 Modify `Backend.API/Controllers/ShiftsController.cs`: delegate closure to `IDailyClosureService` (AD-5)
- [x] 3.7 Modify `Backend.API/Startup/ServiceCollectionExtensions.cs`: register `ITodayExchangeRateProvider` → `TodayExchangeRateProvider` (AD-6)
- [x] 3.8 Re-point `DailyClosureControllerTests`, `Phase7ClosureWithoutRateTests`, `ResidualRemediationLote26Tests`, `SecurityHardeningSprint2Tests` to new service interface
- [x] 3.9 **GREEN**: Add behavior-preservation test: same inputs → identical persisted amounts/status/response; preview 400 preserved
- [x] 3.10 **GREEN**: Add structural test asserting no `DbContext` in `DailyClosureController` or `ShiftsController` constructors/fields (REQ-COC-02)
- [x] 3.11 Verify `CreateClosure` cyclomatic complexity < 10; verify each extracted method < 10 (REQ-COC-03)

## Phase 4a: Closure DTO Boundary (S4a)

- [x] 4a.1 **RED**: Add golden-JSON contract test: capture GET closure response before/after; assert all fields survive with same JSON names
- [x] 4a.2 Create `Sales.Module/DTOs/DailyClosureResponseDto.cs` — immutable record, `init`-only members (AD-14)
- [x] 4a.3 Create `Sales.Module/DTOs/ClosureDetailResponseDto.cs` — immutable record (AD-14)
- [x] 4a.4 Modify `Sales.Module/Services/DailyClosureService.cs`: project closure entity into DTOs via `ShiftReportMapper` (AD-4/14); update `IDailyClosureService` signatures to return DTOs
- [x] 4a.5 Modify `Backend.API/Controllers/DailyClosureController.cs`: return DTOs from `GetClosure` and `CreateClosure` (AD-14)
- [x] 4a.6 Modify `Desktop.Client.Core/Services/DailyClosureClientService.cs`: bind to DTO field names (AD-15)
- [x] 4a.7 **GREEN**: Add reflection test asserting no public setter on DTO members (REQ-ADB-04)
- [x] 4a.8 **GREEN**: Add test asserting no `Sales.Module.Entities` type in `IDailyClosureService` or `DailyClosureController` signatures

> **S4a bookkeeping corrections (fold-in during S4b).** Commit `829778c` labelled the slice `items 3/15/19/20`, but the debt registry maps the **closure** items to `3` and `8`; items `15` (`GetActiveSession`/`OpenSession`/`CloseSession`/`AddTransaction` return `CashDrawerSession`/`CashTransaction`), `19` (`GetHistoryAsync` returns `List<CashTransaction>`; `CashAdvanceResultDto` exposes `CashTransaction`; public `CashDrawerService` methods return entities) and `20` (`GetHistoryAsync` materializes `new CashTransaction { Sale = … }`) are the **drawer** findings owned by S4b. Boxes `4a.5`/`4a.6` are checked but were only partially met and changed no file: `CreateClosure` already returned the immutable `CloseShiftResult` (deviation D1) and `DailyClosureClientService.cs` needed no rebinding (deviation D2, no client reads the closure GET). `S4a-R1` remains open: `DailyClosureService.CreateClosureAsync(DailyClosure)` stays a concrete-only, entity-returning legacy entry point (candidate for deletion). Details in `apply-progress.md` (S4a section, Corrections) and `verify-report.md` (`RESIDUAL-S4a-02/03`, `S4a-R1`).

## Phase 4b: Drawer DTO Boundary (S4b)

- [x] 4b.1 **RED**: Add golden-JSON contract test: capture active drawer session response before/after; assert field parity
- [x] 4b.2 Create `Sales.Module/DTOs/CashDrawerSessionResponseDto.cs` — immutable record (AD-14)
- [x] 4b.3 Create `Sales.Module/DTOs/CashTransactionResponseDto.cs` — immutable record; move from `Backend.API/DTOs/CashDrawerDtos.cs` (AD-14)
- [x] 4b.4 Delete `Backend.API/DTOs/CashDrawerDtos.cs` (AD-14)
- [x] 4b.5 Modify `Sales.Module/Services/CashDrawerService.cs`: return DTOs from all public methods; project via LINQ (AD-14)
- [x] 4b.6 Modify `Sales.Module/Interfaces/ICashDrawerService.cs`: update signatures to return DTOs (AD-14)
- [x] 4b.7 Modify `Backend.API/Controllers/CashDrawerController.cs`: return DTOs from all actions (AD-14)
- [x] 4b.8 Modify WPF client services/views to bind to DTO field names (AD-15)
- [x] 4b.9 **GREEN**: Add test asserting no `CashDrawerSession` or `CashTransaction` entity in response bodies (REQ-ADB-02)
- [x] 4b.10 **GREEN**: Add test asserting `GetHistoryAsync` projects DTOs without materializing entity instances (REQ-ADB-03)

## Phase 5a: CancellationToken Propagation (S5a)

- [x] 5a.1 **RED**: Added `CommandCenter.Tests/Unit/CancellationPropagationTests.cs` — cancelled token → `OperationCanceledException` + no persistence on SQLite real paths (`CreateClosureFromCommandAsync`, `GetClosureAsync`, `CashDrawerService.AddTransactionAsync`); token-forwarding assertions for `CloseShift`/`CreateClosure`/drawer actions. Pre-implementation build failed with 28 signature errors (RED evidence)
- [x] 5a.2 Modify `Backend.API/Controllers/ShiftsController.cs`: `GetCurrentReport`/`GetReportById` accept and forward `CancellationToken` to `GetLatestClosureAsync`/`GetClosureAsync`/`GetCashierDisplayNameAsync` (AD-12)
- [x] 5a.3 Modify `Backend.API/Controllers/CashDrawerController.cs`: all actions accept `CancellationToken`; `ResolveAnchoredRateAsync`/`MapLocalTimesAsync` propagate it (AD-12)
- [x] 5a.4 Modify `Backend.API/Controllers/DailyClosureController.cs`: `GetExpectedTotals`/`GetClosure` accept and forward `CancellationToken` (`CreateClosure` already did) (AD-12)
- [x] 5a.5 Modify `Sales.Module/Services/DailyClosureService.cs`: `GetExpectedTotalsByPaymentMethodAsync` forwards the token to every EF call; legacy `CreateClosureAsync`/`ExecuteClosureCoreAsync`/`GetClosureAsync`/`PersistClosureCoreAsync` accept and forward it; `ExchangeRateResolver.ReadEffectiveTodayRateAsync` gains the parameter and `TodayExchangeRateProvider` forwards to it (AD-12)
- [x] 5a.6 Modify `Sales.Module/Services/CashDrawerService.cs`: every async member accepts and forwards `CancellationToken` to EF Core, advisory locks, transactions and the execution strategy; `CashAdvanceCoordinator` forwards it to the drawer calls (AD-12)
- [x] 5a.7 **GREEN**: `CancellationPropagationTests.TouchedAsyncTypes_DoNotBlockSynchronouslyOnAsyncPaths` — IL scan asserts no `.Result`/`.Wait()`/`Thread.Sleep`/`GetAwaiter().GetResult` on the declared async paths of the touched controllers/services (REQ-ACP-02)

> **S5a fold-ins.** H-14/AD-13 (`MainWindow.OnClosing` → `void` + `Task RunShutdownAsync()` via `SafeFireAndForget`, `Close()` after the await) was pulled into this work unit by explicit orchestrator authorization and landed here; box 5c.2 is marked as delivered-early, the rest of S5c stays pending. Service/interface members with existing call sites take `CancellationToken cancellationToken = default` as their last parameter (matching the pre-existing `IDailyClosureService` style); controller actions take a required token (except `GetHistory`, which keeps the optional `limit` and appends a defaulted token). `ISystemSettingsService.GetSettingAsync` stays CT-less — not in the registered items 4/9/16/21/27; `MapLocalTimesAsync` honors the token with `ThrowIfCancellationRequested` before its settings read. Slice S5a is closed for the items 4/9/16/21/27; S5b+ not started.


## Phase 5b: EF Tuning + Guards/Naming/Comments (S5b)

- [x] 5b.1 Modify `Sales.Module/Services/DailyClosureService.cs`: add `.AsNoTracking()` + `.AsSplitQuery()` to `GetClosureAsync`, `GetHistoryAsync`, latest-closure read (AD-16)
- [x] 5b.2 Modify `Sales.Module/Services/CashDrawerService.cs`: add `.AsNoTracking()` + `.AsSplitQuery()` to `GetActiveSessionWithTransactionsAsync` (AD-16)
- [x] 5b.3 Create `Sales.Module/ClosureStatus.cs`: constants for status labels; reuse `PaymentMethodCurrencyResolver.Usd/LocalCurrency` (AD-17)
- [x] 5b.4 Apply `ArgumentNullException.ThrowIfNull` to `request` and `DeclaredAmounts` in `ShiftsController`/`DailyClosureController` (AD-17)
- [x] 5b.5 Drop unused `_paymentMethodService`/`_settingsService` fields from touched controllers (AD-17)
- [x] 5b.6 Add `...Async` suffix to any async methods missing it in touched services (AD-17)
- [x] 5b.7 Resolve 404 before rate; fix lambda indentation in touched files (AD-17)
- [x] 5b.8 Delete explanatory comments; keep only `8.x-*` traceability markers; no mass Spanish→English translation (AD-18)
- [x] 5b.9 **GREEN**: Add test asserting null `request` throws `ArgumentNullException` from `ThrowIfNull`

> **S5b fold-ins.** `GetHistoryAsync` (AD-16 wording) lives in `CashDrawerService` and already read with `AsNoTracking()` since S4b — the S5b.1 closure tuning landed on `GetClosureAsync` (via `LoadClosureEntityAsync`) and the latest-closure read. Item 29 (`_paymentMethodService`/`_settingsService`) was already resolved by S3: neither field exists in any touched controller (only `CashDrawerController._settingsService`, which has five live reads). Item 28's `...Async` renames landed on the three registered `ShiftsController` actions (`CloseShiftAsync`, `GetCurrentReportAsync`, `GetReportByIdAsync`); the touched services already carried the suffix. Item 37's registered lambda (`ShiftsController` `:84-212`) was structurally removed by S3; the remaining lambda-scope indentation in a touched file (`CashDrawerController.AddTransactionAsync` call) was fixed. Item 36's "rate before `closure == null`" site also died with S3 (the report path no longer resolves a rate); `GetReportByIdAsync` now resolves the 404 before the ownership evaluation. `DailyClosureController` keeps its S2-pinned null-request 400 (`ValidateClosureRequest`) and the `ThrowIfNull(request)` guard sits in `ExecuteCreateClosureAsync` (post-validation continuation) — see `apply-progress.md` S5b deviation D3. `ResolveClosureDate` moved inside the `try` so backdating/future dates map to 400 `ApiBadRequest` instead of the middleware's 409 (S5b.7, S5a GGA registration). Carry-overs: the no-sync-over-async IL scan now enumerates nested state machines (plus a discriminating probe test), and `SalesService.Checkout.cs` forwards its in-scope token to `GetOrCreateActiveSessionAsync`/`RecordSaleChangeAsync`.

## Phase 5c: J Findings (S5c)

- [x] 5c.1 Modify `Backend.API/Controllers/AuthController.cs`: set `Secure = Request.IsHttps` at all three `pos_jwt` cookie sites (H-05)
- [x] 5c.2 Modify `Desktop.Client/MainWindow.xaml.cs`: `OnClosing` returns `void`; shutdown via `Task.RunShutdownAsync()` with `SafeFireAndForget` (AD-13, H-14) — **delivered in S5a (`lcs-s5a-ct-sweep`) by orchestrator authorization**; reflection + IL evidence in `CancellationPropagationTests`
- [x] 5c.3 Modify `Desktop.Client.Core/ViewModels/{CashDrawer,PendingOrders,ExchangeRate,CustomerManagement,CustomerPicker}ViewModel.cs`: add `IDisposable` (H-06)
- [x] 5c.4 Modify `Desktop.Client.Core/ViewModels/MainViewModel.cs`: add disposal + `OnClosed` hook (H-06)
- [x] 5c.5 Modify `Web.Frontend/src/pages/RegisterPage.jsx`: client-side pagination reuse existing `limit`; no server contract change (H-08)
- [x] 5c.6 Re-point existing WPF VM tests for disposal (H-06)
- [x] 5c.7 Run `dotnet test` and `npm test` to verify all J changes pass

> **S5c fold-ins.** H-05 landed as `Secure = Request?.IsHttps ?? false` at all three `pos_jwt` sites (login append, logout delete, change-password delete) — functionally identical to AD-19's `Request.IsHttps` (inside the `Response?.Cookies != null` guard `Request` is never null; the `??` only silences the compiler's nullable analysis, which raised CS8602 on the bare member access and `TreatWarningsAsErrors` turns into a build error). The now-false "Secure siempre" comment blocks were corrected and the matching clause in `PipelineExtensions.cs` updated. H-06: the 5 VMs implement `IDisposable` (the three messenger VMs `UnregisterAll`; `CustomerManagement`/`CustomerPicker` cancel/dispose `_searchCts` via `Interlocked.Exchange` plus `UnregisterAll`); `MainViewModel.Dispose` is now idempotent (`Interlocked` guard) and `MainWindow.OnClosed` disposes the DataContext; `CustomerPickerDialog.Closed` disposes its per-dialog VM (the only manually created one). The four UserControls hosting DI-owned singleton VMs (`CashDrawerView`, `PendingOrdersView`, `ExchangeRateView`, `UsersManagementView`) deliberately carry **no** disposal hook: disposing a singleton on `Unloaded` would kill it for every later navigation; the DI host owns their disposal (see `S5c-D2`). H-08 landed client-side only per AD-19: the 25-per-page in-memory slicing stays and the history fetch reuses the existing `limit` contract (`?limit=HISTORY_FETCH_LIMIT`, clamped server-side at 300) with no `page`/`pageSize`/new endpoint. `RESIDUAL-S5b-06` (registry item 11) closed: `WriteClosedClosureReceiptsAsync` now uses `ArgumentNullException.ThrowIfNull`. The silent-failure trap (append/delete `Secure` flag divergence) is pinned by four new `AuthenticationTests` cases covering both schemes at append, logout-delete and change-password-delete.

## Traceability Table

| Spec | Requirement | Scenarios | Tasks |
|------|-------------|-----------|-------|
| payment-method-currency-classification | REQ-PMC-01 | Report uses classifier; Close classifies via resolver; Diverging name → resolver | 1.1, 1.3, 1.6, 1.10 |
| payment-method-currency-classification | REQ-PMC-04 | Client USD for local method; Currency omitted; Unknown method → 400 | 1.1, 1.3, 1.6, 1.8 |
| payment-method-currency-classification | REQ-PMC-05 | No local heuristic; Diverging name → server label | 1.7, 1.9 |
| api-error-contract | REQ-AEC-01 | Error is ProblemDetails; No anonymous object | 2.1, 2.2, 2.3, 2.4, 2.8 |
| api-error-contract | REQ-AEC-02 | RBAC → 403; Invalid input → 400 | 2.2, 2.3, 2.4, 2.8 |
| api-error-contract | REQ-AEC-03 | Failure logged; Async retry wait | 2.5, 2.10 |
| api-error-contract | REQ-AEC-04 | No discarded fields; Legacy sender ok; Identity from principal | 2.6, 2.7, 2.9 |
| async-cancellation-propagation | REQ-ACP-01 | Token reaches service; Aborted stops work | 5a.1, 5a.2, 5a.3, 5a.4 |
| async-cancellation-propagation | REQ-ACP-02 | Token reaches EF; No sync-over-async | 5a.1, 5a.5, 5a.6, 5a.7 |
| async-cancellation-propagation | REQ-ACP-03 | OnClosing not async void; Close preserved | 5c.2, 5c.7 |
| closure-orchestration-consolidation | REQ-COC-01 | Controller delegates; Service owns transaction | 3.1, 3.4, 3.5, 3.6, 3.9 |
| closure-orchestration-consolidation | REQ-COC-02 | No DbContext in controllers; Authorization gates | 3.10, 3.5, 3.6 |
| closure-orchestration-consolidation | REQ-COC-03 | CreateClosure < 10; Extracted methods < 10 | 3.4, 3.11 |
| closure-orchestration-consolidation | REQ-COC-04 | Same inputs → same closure; Preview 400 preserved | 3.9, 3.11 |
| api-dto-boundary | REQ-ADB-01 | Closure response is DTO; Service returns DTO | 4a.1, 4a.2, 4a.3, 4a.4, 4a.5, 4a.8 |
| api-dto-boundary | REQ-ADB-02 | Drawer session is DTO; Cash advance exposes DTOs | 4b.1, 4b.2, 4b.3, 4b.5, 4b.7, 4b.9 |
| api-dto-boundary | REQ-ADB-03 | History projected, not hand-built | 4b.10 |
| api-dto-boundary | REQ-ADB-04 | No public setter; Client fields survive | 4a.7, 4a.1, 4b.1, 4b.8 |

## Verification

After applying all slices, run:

```bash
# Build — must be 0 errors, 0 warnings (TreatWarningsAsErrors)
dotnet build CommandCenter.slnx -c Release

# Backend tests — must be 100% passing
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj

# Frontend tests and lint — must be 100% passing, lint clean
cd Web.Frontend && npm test && npm run lint

# Coverage gates (measure before deleting/re-pointing tests)
# Core >= 0.70, Sales >= 0.80, Inventory >= 0.72
```

Per-slice verification (apply each slice independently):

```bash
# S1: close-shift classification tests
dotnet test --filter "FullyQualifiedName~CloseShift"

# S2: error contract tests
dotnet test --filter "FullyQualifiedName~Error|FullyQualifiedName~Problem"

# S3: closure orchestration tests
dotnet test --filter "FullyQualifiedName~Closure|FullyQualifiedName~DailyClosure"

# S4a/S4b: DTO boundary tests
dotnet test --filter "FullyQualifiedName~Dto"

# S5a: cancellation propagation tests
dotnet test --filter "FullyQualifiedName~Cancellation"

# S5b: EF tuning + guards tests
dotnet test --filter "FullyQualifiedName~Tuning|FullyQualifiedName~Guard"

# S5c: J findings — full suite + frontend
dotnet test && cd Web.Frontend && npm test && npm run lint
```
