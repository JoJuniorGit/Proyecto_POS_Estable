# Delta for remote-authorization-clients

## ADDED Requirements

### Requirement: Cancellation UX on the waiting terminal (Web + WPF)

The waiting terminal MUST cancel the pending request on the server (best-effort) when the operator presses "Cancelar"; a cancel failure MUST NOT block the local close (the request then expires naturally). The local outcome remains the cancelled result.

#### Scenario: Cancel closes locally and withdraws the request

- GIVEN a wait state with a Pending request
- WHEN the operator cancels
- THEN the terminal closes the wait state immediately
- AND the server request transitions to Cancelled (or expires naturally if the call failed)

### Requirement: Cancelled closure on admin terminals (Web + WPF)

When a queued request is received as resolved with status `Cancelled`, the admin terminal MUST close the notification and show "Solicitud cancelada por el cajero." — never the race message.

#### Scenario: Cashier cancellation closes the admin modal

- GIVEN an admin terminal showing a queued request
- WHEN the requester cancels it
- THEN the modal closes and shows the cancelled note

### Requirement: Reconnect reconciliation for the WPF notification queue

On hub reconnect, the WPF notification queue MUST re-sync every queued request through the status endpoint and apply resolved/expired/cancelled outcomes (it MUST not keep stale actionable modals).

#### Scenario: Resolution lost during a socket drop is reconciled

- GIVEN a queued request resolved while the socket was down
- WHEN the hub reconnects
- THEN the queue reconciles and the stale modal closes with the proper outcome

### Requirement: WPF manual-price trigger for normal products (D6/G1)

The WPF POS MUST offer adding a NORMAL product with a manual price (USD and/or Bs, validated; Bs derived from the current rate when omitted), routed through the existing `AddItemAsync` call so non-elevated users trigger the protected flow and elevated users apply it directly. The cash-advance branch stays untouched.

#### Scenario: Cashier proves a manual price and the flow engages

- GIVEN a cashier adding a normal product with a manual price
- WHEN the backend answers the 403 flow contract
- THEN the wait flow engages (T10 behavior) and an approval retries with the token

### Requirement: Wait-state settlement on rejection/expiry (web infrastructure)

The web wait flow's caller promise MUST settle with a distinguishable outcome on rejection/expiry (`{ ok: false, outcome: 'rejected' | 'expired', reason }`) while the acknowledgment modal stays available to the operator; rejection and manual cancel MUST be distinguishable to the caller.

#### Scenario: Caller learns the rejection without waiting for manual close

- GIVEN a web wait flow in progress
- WHEN a rejection arrives
- THEN the caller promise settles with the rejected outcome and reason
