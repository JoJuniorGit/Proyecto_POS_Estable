# cash-advance-payout-integrity Specification - Delta

## Purpose

Closes registry item 41 and its Web twin: the no-hardcoded-commission rule, already enforced
server-side, extends to the client advance previews. The percentage the operator sees MUST be the
server-resolved value for the selected channel; otherwise the preview fails closed instead of
showing a fabricated 7/10.

Delta mapping against `openspec/specs/cash-advance-payout-integrity/spec.md` (stable IDs assigned
here by document order):

| ID | Existing requirement | Delta action |
|----|----------------------|--------------|
| REQ-CAP-01 | Atomic Payout and Accounting Sale | Unchanged |
| REQ-CAP-02 | Commission Resolved from System Settings | MODIFIED - scope extended to client previews |
| REQ-CAP-03 | Fail-Closed on Unresolvable Commission | MODIFIED - previews fail closed |
| REQ-CAP-04 | Ordering and Rate Anchoring Preserved | Unchanged |
| REQ-CAP-05 | Financial Snapshots Preserved | Unchanged |
| REQ-CAP-06 | No Service Locator | Unchanged |

## MODIFIED Requirements

### Requirement: REQ-CAP-02 Commission Resolved from System Settings

The commission MUST be resolved from a constructor-injected `ISystemSettingsService`.
The system MUST NOT fall back to a hardcoded 10%/7% commission. The prohibition covers the client
advance previews: the percentage shown by `CashAdvanceRegisterViewModel` (WPF) and
`CashAdvanceModal` (Web) MUST be the server-resolved value for the selected channel
(`CashAdvance.TransferCommissionPct` / `CashAdvance.CashCommissionPct`), and neither client MAY
compute a percentage from a local literal.
(Previously: only the server was bound; both clients hardcoded 7% for transfers and 10% for cash.)

#### Scenario: Configured commission is applied

- GIVEN a configured advance commission
- WHEN the advance is processed
- THEN the commission amount MUST use the configured value

#### Scenario: Preview shows the server-resolved percentage

- GIVEN a configured transfer commission of 5.5%
- WHEN the advance preview is opened for a transfer method
- THEN the preview MUST show 5.5%
- AND it MUST NOT show 7 or 10

#### Scenario: Preview follows the selected channel

- GIVEN different configured transfer and cash percentages
- WHEN the operator changes the selected method channel
- THEN the preview MUST show the configured percentage for that channel

#### Scenario: No client literal remains

- GIVEN the source of `CashAdvanceRegisterViewModel` and `CashAdvanceModal`
- WHEN the commission computation is inspected
- THEN no hardcoded 7/10 default MUST remain

### Requirement: REQ-CAP-03 Fail-Closed on Unresolvable Commission

When the configured commission is missing or invalid, the advance MUST be rejected with a
clear error and no cash MAY leave the drawer. The client previews MUST fail closed the same way:
when no valid percentage can be obtained from the server, the preview MUST NOT display or apply a
substituted percentage, and submission MUST be blocked so no advance can be sent with a fabricated
commission.
(Previously: only the server rejection was bound; the clients substituted 7/10.)

#### Scenario: Missing commission rejects the advance

- GIVEN no valid commission setting can be resolved
- WHEN the advance is processed
- THEN the operation is rejected with a clear error
- AND no payout or accounting sale is persisted

#### Scenario: Preview without a resolvable percentage blocks submission

- GIVEN the server cannot resolve a valid commission
- WHEN the advance preview is opened
- THEN it MUST NOT show any default percentage
- AND it MUST block submission

#### Scenario: Previewed percentage equals the charged percentage

- GIVEN a preview rendered with percentage P from the server
- WHEN the advance is processed
- THEN the charged commission MUST use P
