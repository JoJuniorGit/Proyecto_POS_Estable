# Proposal: critical-debt-closeout

## Intent

Close `legacy-debt-cleanup`'s deferred criticals before new features (archived, pass_with_warnings). Three money-visible defects: closure lines hardcoded to `"Balanced"` mixing Bs.S/USD (`WARNING-04`); a WPF advance preview hardcoded to 7/10% beside a fail-closed server (`item 41`); a Web advance filter matching source 4, not `CashAdvance = 2` (`RESIDUAL-S4b-04`). Structural: the 650-line `DailyClosureService` and divergent legacy entry (`S3-06`/`S3-07`).

## Scope

### In Scope

| Item | Evidence/fix |
|---|---|
| WARNING-04 | `DailyClosureService.cs:434-473` (`:470`; units `:463-469`): per-currency difference/status |
| Item 41 (+ Web twin, new) | `CashAdvanceRegisterViewModel.cs:58`, `CashAdvanceModal.jsx:45`; server `CashAdvanceCoordinator.cs:180-199`: server-sourced preview, fail-closed |
| RESIDUAL-S4b-04 | `RegisterPage.jsx:123` (`CashAdvance=2`, `Closing=4`; `CashTransaction.cs:18-20`): filter by contract source |
| S3-06 / S3-07 | `DailyClosureService.cs:112`, `:409-491`, ~18 test sites: cohesive partials (AD-8); delete the legacy entry |
| H-13 (C4) | `ProductDialogViewModel.Pricing.cs:11-43`, `AddProductViewModel.cs:30-53`: decimal `[Range]` |

### Non-Goals

Sessions change (`S5a-R1`, `S5b-R1`, `S5c-R1`). C4: **H-07** (needs schema/migrations) and **H-12** (10 monoliths, HIGH effort; backend slice = S3-06). Plus schema, features, item 40, minor residuals.

## Capabilities

### New Capabilities

- None; all deltas bind to archived capabilities.

### Modified Capabilities

- `payment-method-currency-classification`: MUST use the resolved currency and derive line status, never hardcode it.
- `cash-advance-payout-integrity`: no-hardcoded-10%/7% extends to client previews (server value, fail closed).
- `api-dto-boundary`: transaction `source` MUST resolve from the contract, not a client ordinal.
- `closure-orchestration-consolidation`: one closure-rule implementation; legacy entry point removed or a private adapter.

## Approach

Money-visible first; chained work-unit commits, `ask-on-risk`.

1. **S1** WARNING-04 (response-only; no persisted snapshot change).
2. **S2** item 41 + Web twin -> RegisterPage -> H-13.
3. **S3** split + legacy removal, re-pointing ~18 call sites (may chain S3a/S3b).

## Affected Areas

| Area | Impact |
|---|---|
| `DailyClosureService.cs` (+ partials), `SettingsController.cs` | Modified |
| Clients: `Desktop.Client.Core/{ViewModels,Services}`, `WpfDialogService.Modals.cs`, `RegisterPage.jsx`, `CashAdvanceModal.jsx`, `ProductDialogViewModel.Pricing.cs`, `AddProductViewModel.cs` | Modified |
| Tests: `CommandCenter.Tests/**` | Modified |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Re-point loses behaviour/coverage | High | Re-point in-commit; assert Sales >=0.80 |
| Split diff over the 400-line budget | High | Chain S3a/S3b; tasks forecast |
| Client commission read alters the WPF dialog contract | Med | Fail-closed parity test |

## Rollback Plan

No schema or snapshot change; slices revert alone (S2 with both clients); closures stay frozen.

## Dependencies

None added; `SettingKeys.CashAdvance*CommissionPct`, `ApiProblemResults`, `PaymentMethodCurrencyResolver` exist.

## Success Criteria

- [ ] Build 0/0; backend 100% (>=1227); frontend 100% (>=273), lint clean.
- [ ] Coverage Core >=0.70, Sales >=0.80, Inventory >=0.72.
- [ ] Each item: discriminating test + file:line evidence (WARNING-04, S3-06 mutation-checked).
- [ ] `DailyClosureService` <=500 lines/file, <=McCabe 10; no public legacy entry point.
- [ ] Residuals updated: WARNING-04, 41, S4b-04, S3-06/07; C4 decision.
