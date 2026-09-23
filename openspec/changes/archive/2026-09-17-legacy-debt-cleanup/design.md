# Design: legacy-debt-cleanup

## Technical Approach

Priority-ordered, blocker-first cleanup in eight revertible slices chained under the 400-line review
budget. S1 removes the second currency source of truth from the close path (server resolver + Web page).
S2 recomposes every anonymous error object onto `ApiProblemResults` and deletes the dead close-request
fields. S3 moves the whole closure run (strategy, `Serializable` transaction, effective rate, assembly,
persistence, session rollover) into `IDailyClosureService` and brings `CreateClosure` under McCabe 10.
S4 replaces EF entities with immutable DTOs across the drawer/closure contract. S5 batches CT
propagation, EF read tuning, guards/dead code/comments, and the J findings. No schema change, no
snapshot rewrite, no new dependency.

## Architecture Decisions

| # | Decision | Choice | Rejected | Rationale |
|---|----------|--------|----------|-----------|
| AD-1 | Close classification (REQ-PMC-01/04) | `CloseShift` classifies each declared method with `PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName)`; **delete `DeclaredAmountDto.Currency`** and all reads (`ShiftsController.cs:117,134,185-187`); response `Currency` is the resolver value | Keep the field unread for compatibility | A field that exists gets read again; deleting it makes the second source of truth structurally impossible. ASP.NET Core ignores unknown JSON members, so legacy senders still bind |
| AD-2 | Close-page contract (REQ-PMC-05, item 39) | Page reads `method.currency` only. Contract already exists: `PaymentMethod.Currency` is `[NotMapped]` = `resolver.Resolve(Name)` and `PaymentMethodsController.ToDto` sends it. `getMethodCurrency` becomes `m?.currency === 'USD' ? 'USD' : 'Bs.S'`; payload stops sending `currency` | Keep the `usd\|dolar\|$\|divisa` fallback | The fallback is the divergence the capability exists to kill; `/api/paymentmethods/active` already cache-busts (`_t`) so a stale response is not a real case |
| AD-3 | Shift-close Bs.S arithmetic | `ExpectedAmountBsS` verbatim from the authoritative expected totals; `ActualAmountBsS = declaredNative × rate`; `DifferenceBsS = Actual − Expected`. Status keeps the existing native-currency 0.05 tolerance | Keep `SystemAmount × exchangeRate` | Current code divides then re-multiplies an already-authoritative Bs.S number (`/rate*rate`). Removing the round-trip is exact at the persisted precision and keeps report↔receipt agreement structural |
| AD-4 | Report projection | Extract the per-detail projection of `GetReportById` into one internal mapper used by both the report and the close response, so the close response *is* the report projection | Duplicate the projection in the close path | Makes REQ-PMC-03 / C1-P1 agreement true by construction instead of by review |
| AD-5 | Orchestration owner (REQ-COC-01, items 1/30) | `IDailyClosureService.CreateClosureAsync(CreateClosureCommand, ct)` owns execution strategy + `Serializable` transaction + rate + assembly + persistence + `RolloverSessionAfterClosureAsync`; both `DailyClosureController.CreateClosure` and `ShiftsController.CloseShift` delegate | One orchestrator per controller | Controllers currently run the same envelope twice; the duplication is the debt |
| AD-6 | Rate resolution layering | New `ITodayExchangeRateProvider` in `Core.Interfaces`, implemented in `Backend.API/Services` by delegating to `ExchangeRateResolver.ReadEffectiveTodayRateAsync`; injected into `DailyClosureService` | Call `ExchangeRateResolver` from `Sales.Module`; or move it to Core | `Sales.Module` references only `Core`; calling a `Backend.API` type inverts layering, and moving it drags `InventoryDbContext` into Core. Mirrors existing `ISystemSettingsService` / `ICurrentUserService` |
| AD-7 | Authorization placement (REQ-COC-02) | RBAC, the backdating guard and the preview `dateUtc` 400 stay in controllers; the service never authorizes | Move guards into the service | Spec forbids it; keeps the service callable from jobs |
| AD-8 | Complexity (REQ-COC-03, items 6/13/14) | Extract `TryResolveBackdatedClosureDate`, `ValidateDeclaredMethods`, and `MergeMissingMethods`/`RecalculateTotals` from `ExecuteClosureCoreAsync` (~12). `CreateClosure` ends ≈5 | Suppress the metric | The ceiling is a policy, not a suggestion |
| AD-9 | Error contract (REQ-AEC-01/02) | Anonymous objects → `ApiBadRequest`/`ApiForbidden`/`ApiNotFound` at `ShiftsController.cs:74,106,239,266,278`; `CashDrawerController.cs:157,167,173,178`; `DailyClosureController.cs` legacy sites. **Leave the preview 400 on `Problem(...)`** | Route the preview 400 through `ApiBadRequest` | REQ-AEC-01 freezes it, and the helper emits a dictionary shape, not `ProblemDetails` — switching would regress a spec'd scenario |
| AD-10 | Receipt writers (REQ-AEC-03, item 12) | `TryWrite*WithRetryAsync`: 2 attempts/path, `await Task.Delay(200, ct)`, `catch` logs path + exception via `AppLogger.LogWarn`; `WriteClosedClosureReceipts` → `...Async`, awaited post-commit. 3 target dirs unchanged | Keep sync `Thread.Sleep` | `Thread.Sleep` blocks a request thread on an async path and the empty `catch` hides every failure |
| AD-11 | Dead close fields (REQ-AEC-04, item 35) | Delete `CloseShiftRequest.CashierName/CashierCedula`; `Web.Frontend/src/services/shiftApi.js:10-13` stops sending them — the only sender found; `Desktop.Client.Core/Services/DailyClosureClientService.cs` posts to `api/dailyclosure` and has no `cashierName`/`cashierCedula` sender | Keep them optional | No sender needs them; identity comes from `_currentUserService.UserId`. Verify no `UnmappedMemberHandling.Disallow` is configured |
| AD-12 | CT contract (REQ-ACP-01/02) | `CancellationToken cancellationToken` last on every touched action/service and forwarded to EF Core and `ExchangeRateResolver.ReadEffectiveTodayRateAsync` (gains the parameter). No `.Result`/`.Wait()`/`Thread.Sleep` on async paths | Default to `CancellationToken.None` | An unpropagated token means aborted requests keep writing |
| AD-13 | H-14 `async void OnClosing` | `OnClosing` returns to `void`; dialog + `e.Cancel = true` + `_isShuttingDown` stay synchronous; shutdown moves to `Task RunShutdownAsync()` launched via `SafeFireAndForget`, with `Close()` after the await on the UI context | Move shutdown to `App.OnExit` | Changes observable ordering; the task path fixes the unobserved-exception teardown without altering behavior |
| AD-14 | DTO boundary (REQ-ADB-01..04, items 3/8/15/19/20) | New immutable records in `Sales.Module/DTOs/`: `DailyClosureResponseDto`, `ClosureDetailResponseDto`, `CashDrawerSessionResponseDto`, `CashTransactionResponseDto`. Returned by `IDailyClosureService`, `ICashDrawerService`, `CashAdvanceResultDto` and the touched actions. Local-time fields are computed inside the projector; `Sale.InvoiceNumber` flattened where clients already read it | Two parallel DTO sets | One contract cannot drift from itself. `Backend.API.DTOs.CashTransactionDto` moves to `Sales.Module/DTOs/` (the API copy is deleted) so no duplicate type competes |
| AD-15 | S4 client coupling | The DTO swap and every Web/WPF consumer update land in the **same** slice and revert together | Server-first, clients later | JSON member parity is the whole risk; splitting it produces a window where bindings silently drop fields |
| AD-16 | EF read tuning (items 7/22/31) | `.AsNoTracking()` + `.AsSplitQuery()` on read-only paths (`GetClosureAsync`, `GetHistoryAsync`, `GetActiveSessionWithTransactionsAsync`, latest-closure read). Write paths keep tracking | Blanket `AsNoTracking` | `OpenSessionAsync`/`CloseSessionAsync`/`AddTransactionAsync`/persistence need tracking |
| AD-17 | Guards, dead code, naming (items 11/28/29/32/34/36/37) | `ArgumentNullException.ThrowIfNull`; `...Async` suffix; drop `_paymentMethodService`/`_settingsService`; null-guard `request` and `DeclaredAmounts`; `ClosureStatus` constants + reuse `PaymentMethodCurrencyResolver.Usd/LocalCurrency`; resolve the 404 before the rate; fix lambda indentation | — | Registry items H, behavior-preserving |
| AD-18 | Comments / artifact language (items 5/10/17/23/24/33/38) | Delete explanatory comments, keep only `8.x-*` traceability markers; no mass translation of existing Spanish markers; new comments in English | Rewriting Spanish→English | Translation is pure churn and risks altering marker text |
| AD-19 | J scoping | H-05: `Secure = Request.IsHttps` at all three `pos_jwt` sites (append **and** both deletes — a mismatched flag makes the browser silently retain the cookie). H-06: `IDisposable` on the 5 VMs + `MainViewModel` disposal + `OnClosed` hook. H-08: client-side only (reuse the existing `limit`; no server contract change). H-14 = AD-13 | New pagination endpoint (H-08) | H-06/H-08 widen scope to WPF/Web; maintainer may defer to a C3b follow-up |

## Data Flow

    POST /api/shifts/close  (declared native amounts, no currency)
      ShiftsController: RBAC → dup guard → delegate
        IDailyClosureService.CreateClosureAsync(command, ct)
          ITodayExchangeRateProvider → rate           execute-strategy + Serializable tx
          GetExpectedTotalsByPaymentMethodAsync(ct) → authoritative Bs.S per method
          PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName) → currency
          native → Bs.S · assembly · persist · RolloverSessionAfterClosureAsync · commit
        → DailyClosureResponseDto → ShiftReportMapper → ShiftReportDto → Ok
      post-commit: WriteClosedClosureReceiptsAsync(dto, ct)  [fail-open, logged]

    GET /api/paymentmethods/active → currency = resolver(Name)   ← RegisterClosePage reads this only

## File Changes

| File | Action | Slice |
|------|--------|-------|
| `Backend.API/Controllers/ShiftsController.cs` | Modify — AD-1/4/5/9/11/12/16/17 | S1–S5 |
| `Backend.API/Controllers/DailyClosureController.cs` | Modify — AD-5/7/8/9/12/18 | S2–S5 |
| `Backend.API/Controllers/CashDrawerController.cs` | Modify — AD-9/12/14/16/17/18 | S2–S5 |
| `Backend.API/Controllers/AuthController.cs` | Modify — H-05 cookie `Secure` (3 sites) | S5c |
| `Backend.API/Services/ExchangeRateWriteService.cs` | Modify — `ExchangeRateResolver` CT | S5a |
| `Backend.API/Startup/ServiceCollectionExtensions.cs` | Modify — register provider; AD-18 | S3/S5 |
| `Backend.API/Services/TodayExchangeRateProvider.cs` | Create — AD-6 | S3 |
| `Backend.API/DTOs/CashDrawerDtos.cs` | Delete — holds only `CashTransactionDto` (line 6), which moves to `Sales.Module/DTOs/` | S4b |
| `Core/Interfaces/ITodayExchangeRateProvider.cs` | Create — AD-6 | S3 |
| `Sales.Module/DTOs/{DailyClosureResponseDto,ClosureDetailResponseDto,CashDrawerSessionResponseDto,CashTransactionResponseDto}.cs` | Create — AD-14 | S4 |
| `Sales.Module/Interfaces/CreateClosureCommand.cs` | Create — AD-5 (`namespace Sales.Module.Interfaces`) | S3 |
| `Sales.Module/Interfaces/DeclaredPaymentAmount.cs` | Create — AD-5 (`namespace Sales.Module.Interfaces`) | S3 |
| `Sales.Module/ClosureStatus.cs` | Create — AD-17 | S5b |
| `Sales.Module/Interfaces/{IDailyClosureService,ICashDrawerService}.cs` | Modify — DTO signatures, CT, async receipts | S3–S5 |
| `Sales.Module/Services/DailyClosureService.cs` | Modify — AD-3/5/8/10/12/16/17/18 | S1–S5 |
| `Sales.Module/Services/CashDrawerService.cs` | Modify — AD-12/14/16/18 | S4b/S5 |
| `Sales.Module/Services/ShiftReportMapper.cs` | Create — AD-4 | S1 |
| `Web.Frontend/src/pages/RegisterClosePage.jsx` | Modify — AD-2 | S1 |
| `Web.Frontend/src/services/shiftApi.js` | Modify — AD-1/11 | S1/S2 |
| `Web.Frontend/src/pages/RegisterPage.jsx` | Modify — H-08 client-side | S5c |
| `Desktop.Client/MainWindow.xaml.cs` | Modify — AD-13 | S5c |
| `Desktop.Client.Core/ViewModels/{CashDrawer,PendingOrders,ExchangeRate,CustomerManagement,CustomerPicker,Main}ViewModel.cs` + `Desktop.Client/Views/{CashDrawerView,PendingOrdersView,ExchangeRateView,CustomerPickerDialog,UsersManagementView}.xaml.cs` (`CustomerManagementViewModel` is hosted by `UsersManagementView.xaml:329`) | Modify — H-06 | S5c |
| `Desktop.Client.Core/Services/*` (+ `Desktop.Client/Views/CashDrawerView.xaml` bindings) | Modify — AD-14/15 | S4 |
| `CommandCenter.Tests/**` | Modify — re-pointed + new | all |

## Interfaces / Contracts

```csharp
namespace Core.Interfaces;
public interface ITodayExchangeRateProvider
{
    Task<decimal> GetEffectiveTodayRateAsync(CancellationToken cancellationToken);
}

namespace Sales.Module.Interfaces;
public sealed record DeclaredPaymentAmount(int PaymentMethodId, decimal Amount); // native currency

public sealed record CreateClosureCommand(
    DateTime ClosureDateUtc, string? UserId, string? Observation,
    IReadOnlyList<DeclaredPaymentAmount> Declarations);

public interface IDailyClosureService
{
    Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc, CancellationToken cancellationToken);
    Task<DailyClosureResponseDto> CreateClosureAsync(CreateClosureCommand command, CancellationToken cancellationToken);
    Task<DailyClosureResponseDto?> GetClosureAsync(int id, CancellationToken cancellationToken);
    Task<string?> GetCashierDisplayNameAsync(int userId, CancellationToken cancellationToken);
    Task WriteClosedClosureReceiptsAsync(DailyClosureResponseDto closure, CancellationToken cancellationToken);
}
```

DTO immutability: `record` with `init`-only members and `IReadOnlyList<T>` collections — no public setter,
so `MapLocalTimesAsync`'s mutate-then-serialize pattern is replaced by construction. Every member name
mirrors the entity's current JSON name, including `OpenedAtLocal`, `ClosedAtLocal`, `TransactionTimeLocal`.

## Testing Strategy

| Slice | What to test | Approach |
|-------|--------------|----------|
| S1 | Client sends `currency:"USD"` for a resolver-Bs.S method → amounts, status and persisted closure use Bs.S; currency omitted → succeeds; unknown id → 400 ProblemDetails, nothing persisted | New controller-level tests; re-point the 3 existing `CloseShift` test classes |
| S1 | Report↔receipt agreement (C1 P1): close once, assert `GET /api/shifts/{id}/report` labels and amounts equal the generated TXT/PDF per method | New test using the C1 pattern; blocks the archive |
| S1 | No name/substring classification survives in the page; payload carries no `currency` | New Web unit test on the currency helper + payload shape |
| S2 | Each recomposed site returns `status` == HTTP status; preview 400 unchanged; legacy sender posting `cashierName`/`cashierCedula` → 200 | New xUnit + assertion that no anonymous error object remains |
| S3 | Same inputs → identical persisted amounts/status/response; preview 400 preserved; controller persists nothing | New behavior-preservation test + re-pointed `DailyClosureControllerTests`, `Phase7ClosureWithoutRateTests`, `ResidualRemediationLote26Tests`, `SecurityHardeningSprint2Tests` |
| S3 | Complexity | Record measured McCabe for `CreateClosure` and each extracted method in the verify report; structural test asserts the controller delegates and injects no `DbContext` |
| S4 | Field parity, no dropped JSON member | Golden-JSON contract test for one drawer session + one closure captured before/after; reflection test asserting no public setter; assert no `Sales.Module.Entities` type in touched signatures |
| S5a | Cancelled token → operation-cancelled and nothing persisted; token reaches EF | New tests per touched action/service |
| S5b | Write failure logged, not swallowed; retry wait asynchronous | Capture `AppLogger` output on a forced write failure |
| S5c | `OnClosing` is not `async void`; close behavior preserved; cookie `Secure` follows scheme; VMs dispose | Reflection + WPF tests; re-point existing VM tests |

Coverage gate measured **before** deleting or re-pointing any test; re-pointing lands in the same commit
(Core ≥ 0.70, Sales ≥ 0.80, Inventory ≥ 0.72). Both clients' suites run in the S1/S2/S4 slices.

## Threat Matrix

N/A — no new or renamed routes, no shell, subprocess, VCS/PR automation, executable-file
classification, or process-integration boundary. Every touched endpoint keeps its existing path, verb and
authorization. (The `pos_jwt` cookie change is a transport-flag fix, not a routing or credential-flow
boundary.)

## Migration / Rollout

No migration, no data repair, no snapshot recomputation. Eight chained slices, each verifies and reverts
alone: **S1** zero-trust close (~150 lines, server + Web revert together); **S2** error contract + dead
fields (~120); **S3** closure orchestration (~350, includes test re-pointing); **S4a** closure DTOs
(~250); **S4b** drawer DTOs (~300); **S5a** CT propagation (~150); **S5b** EF tuning + guards/naming/
comments (~250); **S5c** J findings (~200, H-08 possibly deferred). Release note: shifts closed with a
diverging method name now classify by the resolver — a stored arqueo status may flip; closures already
persisted are never recomputed.

## Open Questions

- [ ] `DeclaredAmountDto.PaymentMethodName` is also discarded by the server (overwritten from the
      authoritative totals) — same debt class as item 35, but unregistered and the page still sends it.
      Drop it in S2 or leave it?
- [ ] H-05: is `Secure = Request.IsHttps` acceptable, or must a config key (`Auth:CookieSecure`) gate it
      so HTTPS-only deployments can force the flag? This lowers a security control on plain HTTP LAN.
- [ ] Does the maintainer want H-06 (5 WPF VMs + views) and H-08 in this change, or deferred to a C3b
      follow-up? Both widen the backend-first mandate.
- [ ] AD-3 normalizes `ExpectedAmountBsS` to the authoritative value; exact at the persisted precision
      but technically a change to the stored expression. Accept as behavior-preserving?
