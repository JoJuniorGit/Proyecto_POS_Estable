# Design: critical-debt-closeout

## Technical Approach

Close the three money-visible defects before the structural one, in four revertible slices under the
400-line review budget: **S1** WARNING-04 becomes one shared report-line builder; **S2** one server-resolved
commission read contract consumed by both advance previews (fail-closed); **S3a** the 650-line
`DailyClosureService` splits into cohesive partials; **S3b** the legacy entity path is deleted and its 18
test call sites re-point to the command path. Plus H-13 as decimal-only `[Range]` hygiene. No schema, no
migration, no persisted-snapshot rewrite.

## Architecture Decisions

| # | Decision | Choice | Rejected | Rationale |
|---|----------|--------|----------|-----------|
| AD-1 | WARNING-04 line semantics (REQ-PMC-06) | Extract one `BuildReportDetail(...)` in `DailyClosureService.Rules.cs`; both `BuildDeclaredDetails` (`:315-359`) and `MergeMissingMethodsWithReport` (`:434-473`) call it. Merged declared value is converted to the method's resolved currency before the diff; status derived with the declared path's `0.05m` tolerance. Persisted `ClosureDetail` block (`:449-456`) untouched | Patch only `MergeMissingMethodsWithReport` in place | The defect *is* two copies of one rule; patching one copy guarantees divergence again. Deleting the duplicate `new ShiftReportDetailResult` call site is the fix |
| AD-2 | Commission read contract (REQ-CAP-02/03) | `GET api/cashdrawer/advance-commission?isTransfer={bool}` on `CashDrawerController`, `[Authorize(Roles="Admin,Manager,Cashier")]`; 200 `{isTransfer, percentage}`; unresolvable → `ApiUnprocessableEntity` (422) ProblemDetails. Single rule = one private core; `ProcessAsync` keeps its throwing adapter (message verbatim), preview uses a public `TryGetCommissionPercentageAsync` → `decimal?` | `SettingsController` (class-level `[Authorize(Roles="Admin,Manager")]` → Cashier 403 → would block the operator who actually runs advances); extend an existing settings payload (no commission read exists); compute on the client (that is the defect) | `ProcessAsync` already serves Cashier (`CashDrawerController.cs:251`); the preview must be reachable by the same role or fail-closed becomes fail-blocked. One core ⇒ previewed percentage equals charged percentage by construction |
| AD-3 | Transaction source contract (REQ-ADB-05) | New `Web.Frontend/src/constants/cashTransactionSource.js` with ONE table `[{id, key, label}]`; `RegisterPage.jsx` filter (`:123`) and `getSourceLabel` (`:135-160`) both read it. `advance` → `CashAdvance` (2), label 2 → `Adelanto Efectivo`, 3 → `Ajuste Manual`, 4 → `Cierre Caja`. No ordinal literal in the filter | Fix the filter only | `getSourceLabel` currently maps 2→Ajuste/3→Cierre/4→Adelanto (inverted); fixing one side re-creates the divergence the spec forbids |
| AD-4 | Split shape (REQ-COC-06) | Three cohesive **partials**: `DailyClosureService.cs` (orchestration + queries, ≈326 lines), `DailyClosureService.Rules.cs` (validation, line building, totals, ≈117), `DailyClosureService.Receipts.cs` (`:493-649`, ≈172) | Injected sub-service | Rules are pure statics with no new dependency; a sub-service would change the ctor of every `DailyClosureTestHelper.CreateService` call site for zero cohesion gain. AD-8 precedent kept rule methods as private statics |
| AD-5 | Legacy entry removal (REQ-COC-05) | **Delete** `CreateClosureAsync(DailyClosure)` (`:112`), `ExecuteClosureCoreAsync` (`:123`), `MergeMissingMethodsIntoClosure` (`:409`); move the duplicate-declaration guard from `:125-134` into `ValidateDeclaredMethods` so the guard survives inside the single implementation; re-point the 18 test sites to `CreateClosureFromCommandAsync` | Non-public adapter delegating to the command path | `Sales.Module` exposes `InternalsVisibleTo` only to `Backend.API` (`SalesDbContext.cs:6`), so an internal seam needs a new test-visibility grant; and an entity-in adapter must silently discard the caller's pre-declared `ExpectedAmountBsS`, which the command path derives from the DB — a new footgun. A public adapter is spec-forbidden |
| AD-6 | H-13 decimal validation | `[Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage=…)]` on the 8 `decimal` properties in `ProductDialogViewModel.Pricing.cs:11-43` and the 4 in `AddProductViewModel.cs:30-53` | Keep `double.MaxValue` (overflows `decimal`); drop the attribute | Decimal-only validation; `ProductDialogViewModel : ObservableValidator` calls `ValidateAllProperties()` (`:375`), so the attributes are live. No spec requirement — verified as hygiene only |
| AD-7 | Slice chaining | S1 → S2 → S3a → S3b, each verifies and reverts alone; S2 may chain backend/clients if the diff exceeds the budget | One PR for all four | S3b alone trends ≥250 authored lines; the forecast in `tasks.md` must state `400-line budget risk: High` |
| AD-8 | Verification per slice | Discriminating test per item + one structural/mutation check for the split; coverage measured **before** any delete/re-point and restored in the same commit | Snapshot-only assertions | REQ-PMC-06 and REQ-COC-03/05 are structural claims that only structural tests can falsify |

### AD-1 semantics (exact)

`BuildReportDetail(paymentMethodId, methodName, declaredNative, expectedBsS, rate)`:
`currency = PaymentMethodCurrencyResolver.Resolve(methodName)`; `system = currency == Usd ?
PricingCalculator.ToUSD(expectedBsS, rate) : expectedBsS`; `diff = declaredNative - system`;
`status = |diff| < 0.05m ? Balanced : (diff > 0 ? Surplus : Shortage)`.
Merged caller converts its Bs.S actual to native first (`Usd ? ToUSD(actualBsS, rate) : actualBsS`) — the
current `:463-469` compares a Bs.S value against a USD value, which is the mixed-unit defect. The merged
`ClosureDetail` still persists `actualBsS`/`expectedBsS`/`differenceBsS` unchanged ⇒ response-only.

### AD-5 re-point plan (18 sites, 8 files)

| Class | Sites | Re-point |
|-------|-------|----------|
| Validation-only (`no persist`) | `DailyClosureServiceUnitTests.cs:79,294,343`; `ResidualRemediationLote26Tests.cs:245`; `SecurityHardeningSprint2Tests.cs:155` | Pass `CreateClosureCommand` with declarations; same `ArgumentException` from `RecalculateTotals`/`ValidateDeclaredMethods` |
| Persist-and-assert arithmetic | `DailyClosureServiceUnitTests.cs:54,183,314,363`; `CheckoutAndPaymentTests.cs:339,377,408,442,460`; `CashDrawerClosureTests.cs:189`; `Phase2FinancialAndIntegrityTests.cs:88` | Rewrite: seed known sales, declare *actual* amounts, assert expected/totals from the DB-derived `ExpectedTotalDto` (not the entity's declared expected) |
| Integration flow | `DailyClosureFlowIntegrationTests.cs:58`; `DailyClosureRetryIntegrationTests.cs:72` | Same call swap; `DailyClosureTestHelper.CreateMocks` already returns rate `50m` |

Re-pointed tests now run the command path's receipts + session rollover (fail-open, logged). No test may
assert on receipt files.

## Data Flow

    GET api/cashdrawer/advance-commission?isTransfer=true
      CashDrawerController [Admin,Manager,Cashier]
        CashAdvanceCoordinator.TryGetCommissionPercentageAsync ─→ private core (SettingKeys.*)
        resolved → 200 {isTransfer, percentage}     unresolved → 422 ProblemDetails
      WPF CashAdvanceRegisterViewModel / Web CashAdvanceModal
        null / non-2xx → no percentage shown, CanConfirm=false, submit blocked
      POST api/cashdrawer/cash-advance → ProcessAsync → same core → same percentage

    Closure report line: declarations|merged ─→ BuildReportDetail ─→ ShiftReportDetailResult
                                              (single currency, derived status)

## File Changes

| File | Action | Slice |
|------|--------|-------|
| `Sales.Module/Services/DailyClosureService.cs` | Modify — AD-4/AD-5 (orchestration only) | S3a/S3b |
| `Sales.Module/Services/DailyClosureService.Rules.cs` | Create — AD-1/4/5 | S1/S3a |
| `Sales.Module/Services/DailyClosureService.Receipts.cs` | Create — AD-4 (move `:493-649`) | S3a |
| `Sales.Module/Services/CashAdvanceCoordinator.cs` | Modify — AD-2 (single core + public `TryGetCommissionPercentageAsync`) | S2 |
| `Backend.API/Controllers/CashDrawerController.cs` | Modify — AD-2 (new GET) | S2 |
| `Web.Frontend/src/constants/cashTransactionSource.js` | Create — AD-3 | S2 |
| `Web.Frontend/src/pages/RegisterPage.jsx` | Modify — AD-3 | S2 |
| `Web.Frontend/src/components/register/CashAdvanceModal.jsx` | Modify — AD-2 (`:45,166,182`) | S2 |
| `Desktop.Client.Core/Services/{ICashDrawerService,CashDrawerService}.cs` | Modify — AD-2 client read | S2 |
| `Desktop.Client.Core/ViewModels/CashAdvanceRegisterViewModel.cs` | Modify — AD-2 (`:58`) | S2 |
| `Desktop.Client/Services/WpfDialogService{,.Modals}.cs` | Modify — AD-2 wiring | S2 |
| `Desktop.Client.Core/ViewModels/{ProductDialogViewModel.Pricing,AddProductViewModel}.cs` | Modify — AD-6 | S2 |
| `CommandCenter.Tests/**` | Modify — AD-8 (+18 re-points) | S1–S3b |

## Interfaces / Contracts

```csharp
// Sales.Module/Services/CashAdvanceCoordinator.cs
public async Task<decimal?> TryGetCommissionPercentageAsync(bool isTransfer, CancellationToken cancellationToken = default);

// Backend.API — CashDrawerController.cs (kebab-case literal, [Route("api/[controller]")])
[HttpGet("advance-commission")]
[Authorize(Roles = "Admin,Manager,Cashier")]
public async Task<ActionResult<CashAdvanceCommissionDto>> GetAdvanceCommission([FromQuery] bool isTransfer, CancellationToken cancellationToken);

public sealed record CashAdvanceCommissionDto(bool IsTransfer, decimal Percentage);

// Web: Web.Frontend/src/constants/cashTransactionSource.js
export const CashTransactionSource = Object.freeze({ Opening:0, SalePayment:1, CashAdvance:2, ManualAdjustment:3, Closing:4, CashIn:5, CashOut:6 });
export const SOURCE_DEFINITIONS = [ /* [{ id, key, label }] — labels and filters read this one array */ ];
export function getSourceLabel(source);  // by id or enum name
// WPF VM: optional ctor source keeps existing tests compiling:
// CashAdvanceRegisterViewModel(List<PaymentMethodDto>, decimal, decimal exchangeRate = 1.0m, ICashDrawerService? cashDrawer = null)
```

## Testing Strategy

| Slice | Discriminating tests | Mutation / structural check | Budget input |
|-------|----------------------|----------------------------|--------------|
| S1 | Merged USD line: declared/system/diff all USD, no Bs.S mixed; merged cash line with expected≠0 → `Shortage`, never `Balanced`; within `0.05m` → `Balanced`; re-read a pre-existing closure → persisted Bs.S fields byte-identical | Reflection/IL scan: exactly one `new ShiftReportDetailResult` in `Sales.Module` | ≈120 |
| S2 | Preview 5.5% shows 5.5 (not 7/10); channel switch returns the other channel's value; unresolvable → 422 + submit blocked; previewed P == charged P; Cashier can read | `advance` filter includes 2 / excludes 4; `getSourceLabel(2)==='Adelanto Efectivo'`; source-scan test: no bare source ordinal in `RegisterPage.jsx` | ≈220 (may chain) |
| S3a | None (pure move) — all existing closure tests green | Every file ≤500 lines; every method McCabe <10 measured and recorded; mutation check: re-introduce a duplicated rule in a throwaway local run and assert a test fails | ≈80 + tests |
| S3b | Post-delete: no public member accepts/returns `DailyClosure` to create a closure; the 18 re-pointed tests assert the same behaviors through the command path | Reflection: `ExecuteClosureCoreAsync`/`MergeMissingMethodsIntoClosure` absent; duplicate-declaration guard still throws "duplicados" | ≈300–350 (**High**) |

## Threat Matrix

No git/shell/subprocess/VCS/PR/executable-file/process-integration boundary exists, so those rows are N/A
(no `git -C`, no commit/push/PR command, no file-classification or execution change). AD-2 adds an HTTP
route, so one row is added and applicable.

| Boundary | Applicability | Design response | Planned RED tests |
|---|---|---|---|
| Documentation-like paths | N/A — no executable-file classification | — | — |
| Git repository selection / commit state / push state / PR commands | N/A — no VCS or PR automation | — | — |
| **New HTTP route authorization** | Applicable — `GET api/cashdrawer/advance-commission` | Safe: role in `Admin,Manager,Cashier`, read-only, returns the resolved value. Failure: unresolved setting → 422 with no percentage; unauthorized → 401/403 and no business data | Cashier → 200 with configured pct; unconfigured → 422 and body carries no default; anonymous/`Driver` → 401/403 |

## Migration / Rollout

No migration, no data repair, no snapshot recomputation. Rollback per slice, source-only:
**S1** revert helper + its two call sites — persisted closures never touched; **S2** revert backend route, the
coordinator read, and both clients together (contract pair) — previews return to the old literal only if the
whole slice reverts; **S3a** revert to the monolith (pure move, no behavior); **S3b** revert the deletions and
the 18 re-points together — the deleted legacy entry returns with them. Release note: a merged closure line
whose difference is non-zero now reports `Surplus`/`Shortage` instead of `Balanced`, and USD merged lines are
single-currency; closures already persisted are never recomputed.

## Open Questions

- [ ] S3b rewrites 18 legacy tests whose inputs (entity-carried `ExpectedAmountBsS`) do not exist in the
      command contract. Confirm the maintainer accepts semantic rewrites of those tests, not a mechanical
      call swap. If not, the fallback is a non-public adapter **plus** `InternalsVisibleTo("CommandCenter.Tests")`.
- [ ] AD-2 fail-closed status: 422 (`ApiUnprocessableEntity`) chosen vs 409 (`ApiConflict`). Confirm.
- [ ] Web preview error UX: on a non-2xx read, block with a fixed message (chosen) or add a retry affordance?
- [ ] AD-1: merged USD declared value uses `PricingCalculator.ToUSD` (4-dp rounding) for symmetry with the
      system amount. Acceptable, or must the merged declared value stay unrounded?
- [ ] AD-6: is `decimal` max the wanted bound, or should a business ceiling (e.g. `1_000_000`) apply?
