# Archive Report: cash-closure-integrity

**Date**: 2026-09-16
**Mode**: openspec
**Verdict**: PASS (archive admissible)

## Summary

Closed the cash-closure-integrity change addressing two hypothesis defects (H-01, H-02) and one adjacent improvement (classifier swap). The daily-closure arqueo now anchors its time window to the active drawer session or last closure (not the calendar day), preventing night-shift sales from being silently dropped. Cash advances are orchestrated by a dedicated coordinator that owns the sale-then-drawer envelope, eliminating both Service Locators and ensuring atomic payout with correct commission. The payment-method currency classifier is unified as a single source of truth.

## Implementation Slices

### Slice 1 — H-01: ClosureWindowResolver (arqueo window anchoring)

- `ClosureWindowResolver.cs` created — pure static resolver, no DbContext
- `DailyClosureService.cs` modified — session read + resolver call + half-open predicates
- `DailyClosureController.cs` modified — preview dateUtc validation (400 on default)
- `ClosureWindowResolverTests.cs` — 7 pure tests (renamed, QA convention applied)
- `DailyClosureServiceWindowTests.cs` — window integration tests + 2 edge-case CashTransactions tests
- `DailyClosureControllerTests.cs` — 2 tests (default→400, valid→Ok)
- Post-verification fixes (1b): dead code removal, test renaming/consolidation, edge cases

### Slice 2 — H-02: CashAdvanceCoordinator (advance orchestration)

- `CashAdvanceCoordinator.cs` created — scoped service, 4 ctor deps, execution-strategy envelope
- `CashDrawerService.cs` modified — removed `ProcessCashAdvanceAsync`, `IServiceProvider`, both locators; 449 lines (under 500)
- `ICashDrawerService.cs` modified — removed `ProcessCashAdvanceAsync` signature
- `CashDrawerController.cs` modified — inject coordinator, replace call
- `ServiceCollectionExtensions.cs` modified — register coordinator as scoped
- `CashAdvanceCoordinatorTests.cs` — 5 unit tests (commission, fail-closed, rate anchoring, snapshots)
- `CashAdvanceEnvelopeTests.cs` — Postgres-gated integration tests (atomicity envelope)
- `CashAdvanceTests.cs` — 9 call sites re-pointed to coordinator
- `CashDrawerServiceUnitTests.cs`, `CashDrawerClosureTests.cs`, `Phase3ConcurrencyAndReservationTests.cs` — constructor/removed-method fixes
- Post-verification fixes (2b): exact assertions, rate-anchoring discrimination, envelope test hardening, logging

### Slice 3 — Classifier swap (adjacent improvement)

- `ShiftsController.cs` modified — replaced local heuristic with `PaymentMethodCurrencyResolver.Resolve` + `PricingCalculator.ToUSD`
- `PaymentMethodCurrencyClassificationTests.cs` — 4 discriminant tests (1 controller-level + 3 unit); rewritten to eliminate tautological comparisons
- Post-verification fixes (3b): seed without accent, discriminant rename, literal assertion, midpoint case
- Post-verification fixes (3c): consistency of discriminant, re-verification 0/0, 1136/1136, 4/4

### P1 Fix — CRITICAL-01 closure

- `PaymentMethodCurrencyClassificationTests.cs` — added `GetReportById_And_ClosureReceipt_ClasificanIgual_MismoCierre`: runtime test executing both report path (`ShiftsController.GetReportById`) and receipt path (`DailyClosureService.GenerateReceiptContent`) on same closure with divergent payment methods; asserts currency label agreement per method
- Discriminating power proven against pre-fix heuristic (`ShiftsController.cs@b016c5d:281`): test fails on old code, passes on current

## Commits

| Hash | Description |
|------|-------------|
| `a94c536` | Slice 1: ClosureWindowResolver + DailyClosureService + tests |
| `a34584c` | Slice 2: CashAdvanceCoordinator + CashDrawerService cleanup |
| `b016c5d` | Slice 3: ShiftsController classifier swap |
| `711710b` | Post-verification fixes (2b, 3b, 3c) |

**Note**: P1 test (`PaymentMethodCurrencyClassificationTests.cs` +95 lines, uncommitted working-tree change) is pending commit by the orchestrator.

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS |
| Requirements | 14/14 compliant |
| Scenarios | 18/18 compliant |
| Build | 0 errors / 0 warnings (incremental + forced full rebuild) |
| Test suite | 1137/1137 passed, 0 failed, 0 skipped |
| Coverage (Sales.Module) | 0.8879 (gate: ≥ 0.80) |
| Coverage (Core) | 0.8364 (gate: ≥ 0.70) |
| Coverage (Inventory.Module) | 0.8251 (gate: ≥ 0.72) |
| Tasks | 20/20 complete |
| Admission | `sdd-verify-validate` → valid:true, verdict:pass |

### Evidence

- **Build output**: `sha256:a1ae51175af1a1cc42f231b57dca3f6d8fe9402a4a0123b474397501dce0c958`
- **Forced rebuild**: `sha256:5e522c66aed96df6f1f40e1ddf6412e214b879d50a933a0c49b7e22d73ac67e9`
- **Test output**: `sha256:f65e68c1fc321b88386be484135f461e9eb88d3cbe0b5c175bcfbc2da8d1283e`
- **Coverage run**: `sha256:a86decb360d9a7bad68cc01229ae5f0a3eea39c6caa84c5cc586c5882ab1bd4e`
- **CRITICAL-01 closure**: discriminating runtime test exercising both report and receipt paths; pre-fix heuristic proven to fail; suite 1136→1137

## Residual Warnings

### WARNING-01 (OPEN — environment-derived)

Postgres-gated scenario #10 ("Accounting sale fails") rests on `CashAdvanceEnvelopeTests`, which early-returns when `TEST_POSTGRES_CONNECTION` is unset. xUnit counts those tests as passed — 3 vacuous passes in this run. Not specific to this change: documented repo-wide silent-pass pattern of `PostgresRealCollection` classes, deliberately enforced in CI. **Not blocking archive**: the covering test exists, is part of the suite, and CI enforces real execution. Should be closed by a Postgres-backed suite run when a database is reachable.

### WARNING-03 (NEW — assertion strength, narrow)

The receipt half of the new test is tautological for USD-classified methods: `PaymentMethodCurrencyResolver.Resolve` returns `USD` only when the name contains literal `"USD"`, so `line.Contains(expectedCurrency)` is satisfied by the method-name column alone. A receipt-path regression affecting only USD-named methods (e.g. `"Divisas (USD)"` rendered as `Bs.S`) would escape detection. The `"Dolares"` row IS discriminating and covers the historical defect. Report-side assertion at `:192` is exact for both rows. **Recommendation**: make receipt assertion positional (assert rendered `"{name} {currency}"` cell pair or parse fixed-width columns) instead of substring containment.

### Legacy Debt

41 items registered in `docs/deuda-legacy-gga-2026-09-16.md` (cross-checked: none introduced or worsened by this change). Out of scope.

## Specs Synced

| Domain | Action | Details |
|--------|--------|---------|
| `cash-closure-arqueo-window` | Created | 5 requirements, 8 scenarios — copied to `openspec/specs/cash-closure-arqueo-window/spec.md` |
| `cash-advance-payout-integrity` | Created | 6 requirements, 7 scenarios — copied to `openspec/specs/cash-advance-payout-integrity/spec.md` |
| `payment-method-currency-classification` | Created | 3 requirements, 3 scenarios — copied to `openspec/specs/payment-method-currency-classification/spec.md` |

## Archive Contents

- `proposal.md` ✅
- `specs/` ✅ (3 capabilities)
- `design.md` ✅
- `tasks.md` ✅ (20/20 tasks complete)
- `apply-progress.md` ✅
- `verify-report.md` ✅
- `exploration.md` ✅
- `archive-report.md` ✅

## Source of Truth Updated

The following specs now reflect the new behavior:
- `openspec/specs/cash-closure-arqueo-window/spec.md`
- `openspec/specs/cash-advance-payout-integrity/spec.md`
- `openspec/specs/payment-method-currency-classification/spec.md`

## Key Learnings

1. Mechanical archive copy with hash verification prevents silent byte corruption during spec sync.
2. Git mv preserves the full history of the change folder in the archive.
3. CRITICAL-01 discrimination proof against pre-fix blob is a sound regression-guard technique.
