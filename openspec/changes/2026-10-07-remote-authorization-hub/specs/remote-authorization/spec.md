# Delta for remote-authorization

## ADDED Requirements

### Requirement: Authorization request creation via hub and REST

`AuthorizationHub.RequestAuthorization` and REST `POST /api/authorizations` MUST create an authorization request for an authenticated non-elevated user with access to the target sale, MUST enforce at most one Pending request per `(requester, sale, action)` returning the existing request with its remaining lifetime, and MUST broadcast an enriched `AuthorizationRequested` event to every connected `Admin`/`Manager` connection. `Driver` MUST remain blocked from sales. Elevated users (`Admin`/`Manager`) MUST be rejected with "Los usuarios con rol de Administrador o Supervisor no requieren autorización.".

#### Scenario: Cashier requests remote authorization for a manual price override

- GIVEN an authenticated Cashier with an open sale
- WHEN the terminal emits `RequestAuthorization` with action `ManualPriceOverride`, the sale id and the operation context
- THEN a Pending request is persisted with `expiresAt = createdAt + 60 s`
- AND every connected Admin/Manager receives `AuthorizationRequested` with cashier name, sale id, action and context detail

#### Scenario: Duplicate pending request is merged

- GIVEN a Pending request for the same cashier, sale and action
- WHEN another request for the same tuple arrives
- THEN the existing request id and its remaining lifetime are returned
- AND exactly one Pending row exists (partial unique index)

#### Scenario: Elevated user cannot create a request

- GIVEN an Admin or Manager
- WHEN request creation is attempted
- THEN it is rejected with the explicit message
- AND no request is persisted

### Requirement: Race-safe remote resolution

`ResolveAuthorization` (hub) and REST `POST /api/authorizations/{id}/resolve` MUST be restricted to `Admin`/`Manager`, MUST resolve atomically so the first response wins (approve or reject), MUST broadcast `AuthorizationResolved` to the requester and to all elevated connections, and any subsequent resolution attempt MUST fail with exactly "Esta solicitud ya fue resuelta por {resolverName}.".

#### Scenario: First admin approves

- GIVEN a Pending request
- WHEN the first Admin approves it (optionally with a reason)
- THEN the request becomes Approved with `resolvedByName`
- AND the requester receives `AuthorizationResolved` carrying an ephemeral token
- AND all elevated connections receive `AuthorizationResolved` to close their notification

#### Scenario: Second admin loses the race

- GIVEN an already resolved request
- WHEN a second Admin attempts to resolve it
- THEN the attempt fails with the exact message "Esta solicitud ya fue resuelta por {resolverName}."
- AND the stored resolution is unchanged

#### Scenario: Rejection with reason

- GIVEN a Pending request
- WHEN an Admin rejects it with a reason
- THEN the request becomes Rejected and the requester is unlocked with the rejection outcome and reason

### Requirement: Ephemeral single-use authorization token

On approval the backend MUST issue a signed ephemeral token valid for 60 seconds from resolution, bound to requester, action, sale and canonical operation-context hash, audience `pos:authorization`. Delivery MUST be restricted to the requester: the approval push, a requester-scoped `GET /api/authorizations/{id}` recovery that re-issues with the same window and NEVER extends past `resolvedAt + 60 s`, or the local-resolve response. Consumption MUST be atomic and single-use; invalid, reused, expired or mismatched tokens MUST be rejected without executing the protected operation.

#### Scenario: Token is consumed exactly once

- GIVEN an Approved request within its token window
- WHEN the protected operation retries with the token
- THEN the token is claimed atomically and the operation proceeds
- AND a second use of the same token is rejected

#### Scenario: Context mismatch is rejected

- GIVEN a token approved for a specific product, quantity and custom price
- WHEN the operation retries with a different payload
- THEN the token is rejected and the sale is not mutated
- AND the cashier must request a new authorization

#### Scenario: Recovered token never extends the window

- GIVEN an Approved request whose push was lost
- WHEN the requester queries its status after `resolvedAt + 60 s`
- THEN no usable token is returned

### Requirement: Request expiry at 60 seconds

A Pending request MUST transition to `Expired` when 60 seconds elapse without resolution. Expiry MUST be enforced lazily on resolve/consume paths and by a background sweep. Expiry MUST append an audit row and push `AuthorizationExpired` to the requester and elevated connections. A resolution attempt after expiry MUST fail with "La solicitud expiró; debe generarse una nueva.".

#### Scenario: No admin responds within 60 seconds

- GIVEN a Pending request and no connected admin response
- WHEN 60 seconds elapse
- THEN the request becomes Expired
- AND the cashier terminal receives the expiry push and shows "Expirada"

#### Scenario: Late approval after expiry

- GIVEN an Expired request
- WHEN an Admin attempts to approve it
- THEN the attempt fails with the explicit expired message
- AND no token is issued

### Requirement: Local fallback via supervisor credentials

`POST /api/authorizations/{id}/local-resolve` MUST verify supervisor credentials entered on the cashier terminal (password hash + `Admin`/`Manager` role), MUST reuse the login lockout policy, MUST resolve the request atomically with mode `Local`, MUST return the ephemeral token and supervisor name, and MUST fail closed with "Credenciales inválidas o sin privilegios para autorizar." without disclosing whether the user exists.

#### Scenario: Supervisor approves locally without network

- GIVEN a Pending request and a supervisor physically at the cashier terminal
- WHEN the supervisor enters valid credentials
- THEN the request resolves with mode Local and an audit row records the supervisor
- AND the token is returned to the terminal and the protected action proceeds

#### Scenario: Wrong credentials fail closed

- GIVEN a Pending request
- WHEN credentials are wrong
- THEN the generic invalid-credentials message is returned
- AND the failed attempt counts toward the login lockout policy

#### Scenario: Local attempt on an expired request

- GIVEN an Expired request
- WHEN local resolution is attempted
- THEN it fails with the explicit expired message and no token is issued

### Requirement: Immutable authorization audit

Every terminal transition (Approved/Rejected/Expired) MUST append exactly one `AuthorizationAudit` row associating: action, executing cashier, resolving admin id and name (remote or local; null on expiry), mode, reason, terminal and timestamps. Audit rows MUST be append-only: no application surface updates or deletes them, and on PostgreSQL a database trigger MUST reject UPDATE and DELETE.

#### Scenario: Remote approval records the resolver

- GIVEN an approved request resolved remotely
- WHEN the audit is inspected
- THEN one row exists with action, cashier, resolver id/name, mode Remote and timestamp

#### Scenario: Expiry records a null resolver

- GIVEN an expired request
- WHEN the audit is inspected
- THEN one row exists with a null resolver and the expiry timestamp

#### Scenario: Audit rows cannot be mutated on PostgreSQL

- GIVEN a persisted audit row (Postgres integration environment)
- WHEN an UPDATE or DELETE is attempted directly in SQL
- THEN the database trigger raises an exception and the row is unchanged

### Requirement: Protected action enforcement (ManualPriceOverride)

POST `/api/sales/{id}/items` MUST accept `X-Authorization-Token` for non-elevated users when a custom unit price is present: a valid token MUST be consumed and the item added with the custom price through the existing `isPriceOverrideAuthorized` hook. Absence, reuse, expiry or mismatch MUST return 403 ProblemDetails with `authorizationRequired=true` and `authorizationAction="ManualPriceOverride"` and MUST NOT mutate the sale. Elevated users MUST keep current behavior. The token check stays before idempotency resolution; an idempotent replay of a successful attempt never re-checks the token. A protected operation that fails after consumption MUST NOT restore the token (fail-closed). Sale cancellation is explicitly OUT of v1 (maintainer veto, tracker L6): cashiers keep the current behavior.

#### Scenario: Price override succeeds with a valid token

- GIVEN an Approved request for `ManualPriceOverride` matching the payload
- WHEN the cashier retries the add with `X-Authorization-Token`
- THEN the item is added with the custom price and the token is consumed

#### Scenario: Price override without token is refused with the flow contract

- GIVEN a Cashier adding an item with a custom price and no token
- WHEN the request runs
- THEN it returns 403 with `authorizationRequired=true` and `authorizationAction="ManualPriceOverride"`
- AND the sale is not mutated

#### Scenario: Elevated users are unaffected

- GIVEN an Admin or Manager
- WHEN a custom price add runs
- THEN it proceeds without any authorization token
