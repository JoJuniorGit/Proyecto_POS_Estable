# Delta for remote-authorization

## ADDED Requirements

### Requirement: Requester cancellation of a pending request

`POST /api/authorizations/{id}/cancel` MUST be available to the authenticated requester and MUST transition their Pending request atomically to `Cancelled` (new terminal status; audited exactly once with null resolution mode/resolver and the resolution timestamp). The requester connection and all elevated connections MUST receive `AuthorizationResolved` with status `Cancelled` and NO token. Cancelling a request that was already resolved or expired MUST fail with the same exact messages as resolution ("Esta solicitud ya fue resuelta por {resolverName}." / "La solicitud expiró; debe generarse una nueva."), and a non-requester MUST be rejected (403).

#### Scenario: Cashier abandons the operation and cancels

- GIVEN a Pending request created by a cashier
- WHEN the cashier cancels it
- THEN the request becomes Cancelled with one audit row (no resolver)
- AND elevated connections receive the Cancelled closure so their modals close

#### Scenario: Cancellation after resolution loses to the race

- GIVEN an already Approved request
- WHEN the requester attempts to cancel
- THEN it fails with exactly "Esta solicitud ya fue resuelta por {resolverName}."
- AND the resolution is unchanged

#### Scenario: Only the requester can cancel

- GIVEN a Pending request owned by cashier A
- WHEN cashier B attempts to cancel it
- THEN the attempt is rejected and the request stays Pending

### Requirement: Atomically bound token consumption

`TryConsumeAsync` MUST evaluate requester, action, sale and context-hash binding AND the single-use state within the same atomic claim (no pre-read TOCTOU), while preserving the precise failure outcomes for classification.

#### Scenario: Binding mismatch cannot be raced into a consume

- GIVEN an Approved token whose action/sale/context were tampered with
- WHEN consumption is attempted concurrently
- THEN the atomic claim rejects it and the request remains unconsumed

### Requirement: Lazy-expiry race classification

When the lazy-expiry path loses its claim race (0 rows affected), the outcome MUST be re-read before classification so a concurrent approval is never reported as `Expired`.

## MODIFIED Requirements

### Requirement: Protected action enforcement (ManualPriceOverride)

POST `/api/sales/{id}/items` MUST accept `X-Authorization-Token` for non-elevated users when a custom unit price is present: a valid token MUST be consumed and the item added with the custom price through the existing `isPriceOverrideAuthorized` hook. Absence, reuse, expiry or mismatch MUST return 403 ProblemDetails with `authorizationRequired=true` and `authorizationAction="ManualPriceOverride"` and MUST NOT mutate the sale. Elevated users MUST keep current behavior. The token gate runs before idempotency resolution for negative outcomes; when the token is already consumed the endpoint falls through to idempotency resolution so a retried successful attempt replays its stored response (HIT) instead of failing, and a MISS in that state returns the 403 contract without executing. When the retried request omits the mandatory `Idempotency-Key`, the endpoint's 400 key contract applies first (qualified: an already-consumed token plus a missing key is a malformed request, not the 403 case). Consumption precedes execution: a failed protected operation does not restore the token (fail-closed). Sale cancellation is explicitly OUT of v1 (maintainer veto): cashiers keep the current behavior.

### Requirement: Pending dedupe covers NULL sale targets

On PostgreSQL the pending partial unique index MUST treat NULL `SaleId` values as duplicates (`NULLS NOT DISTINCT`), so concurrent creates for `(user, NULL, action)` cannot double-insert; SQLite keeps its NULL-distinct semantics and the sequential service dedupe.

#### Scenario: Concurrent NULL-sale duplicate is rejected on PostgreSQL

- GIVEN a Pending request with `SaleId = NULL`
- WHEN a second insert for the same `(user, NULL, action)` is attempted on PostgreSQL
- THEN the unique index raises 23505 and the service path returns the existing request
