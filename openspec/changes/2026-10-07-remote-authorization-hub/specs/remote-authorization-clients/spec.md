# Delta for remote-authorization-clients

## ADDED Requirements

### Requirement: Blocking wait state in the cashier terminal (WPF v1; Web infrastructure)

(Scope note, tracker L6: the Web manual-price UI is vetoed, so v1 has no Web-side trigger; the Web wait flow remains specified as infrastructure for future protected actions and is validated with simulated refusals.)

When the backend refuses a protected action with `authorizationRequired`, the terminal MUST block the operation into the state "Esperando autorización remota...", showing the operation context and a live countdown to `expiresAt`, MUST offer a "Autorización Local" action, MUST allow cancelling the operation, and MUST resume the protected action automatically upon approval using the delivered token.

#### Scenario: Cashier wait state appears and blocks the action (Web)

- GIVEN a Cashier attempts a protected action in the Web terminal
- WHEN the backend answers 403 `authorizationRequired`
- THEN the terminal shows "Esperando autorización remota..." with the operation context and countdown
- AND the protected action does not execute until authorization arrives

#### Scenario: Cashier wait state appears and blocks the action (WPF)

- GIVEN a Cashier attempts a protected action in the WPF terminal
- WHEN the backend answers 403 `authorizationRequired`
- THEN a blocking dialog shows "Esperando autorización remota..." with countdown
- AND the POS remains blocked until resolution or cancel

#### Scenario: Approval resumes the action with the token

- GIVEN a wait state with a delivered token
- WHEN the approval event arrives
- THEN the terminal retries the protected action with `X-Authorization-Token`
- AND the action completes and the wait state closes

### Requirement: Enriched admin notification and remote resolution (Web + WPF)

Every connected Admin/Manager terminal (Web and WPF) MUST surface an interrupting notification with cashier, action, sale id and context detail (e.g. "El cajero {cashier} solicita autorización para {acción} en la Factura #{saleId}. {detalle}"), offering "Aprobar" and "Rechazar" with an optional reason. Real-time resolution from one terminal MUST close the notification on all others, and a late responder MUST be shown exactly "Esta solicitud ya fue resuelta por {resolverName}.".

#### Scenario: Notification reaches both clients

- GIVEN Admin/Manager sessions connected in Web and WPF
- WHEN a request is created
- THEN both terminals show the enriched notification with approve/reject actions

#### Scenario: One admin resolves and all modals close

- GIVEN the same request visible in two admin terminals
- WHEN one approves it
- THEN the other terminal closes its notification in real time
- AND a late approval attempt shows the exact race message

### Requirement: Rejection and expiry outcomes on the waiting terminal

A rejection MUST unlock the wait state with "Solicitud rechazada." plus the reason when present. Expiry MUST switch the wait state to "Expirada" with a retry action that starts a new request.

#### Scenario: Rejection unlocks with reason

- GIVEN a Cashier wait state
- WHEN a rejection with reason arrives
- THEN the terminal unlocks and shows the rejection outcome and reason

#### Scenario: Expiry switches the state and allows retry

- GIVEN a Cashier wait state with the countdown at zero
- WHEN no resolution arrived
- THEN the terminal shows "Expirada" and offers retry
- AND retry creates a new authorization request

### Requirement: Local authorization from the cashier terminal

"Autorización Local" MUST open a supervisor credential form on the cashier terminal. Success MUST unlock and execute the protected action without remote approval. Invalid credentials MUST show "Credenciales inválidas o sin privilegios para autorizar." and keep the wait state; the login lockout policy MUST apply.

#### Scenario: Supervisor authorizes in person

- GIVEN a wait state and a supervisor at the terminal
- WHEN valid credentials are submitted
- THEN the wait state closes, the action executes with the returned token
- AND the audit records mode Local

#### Scenario: Invalid credentials keep the wait state

- GIVEN a wait state
- WHEN wrong credentials are submitted
- THEN the explicit error is shown and the terminal keeps waiting
- AND repeated failures count toward lockout

#### Scenario: Zero-trust refusal starts the flow (capability)

- GIVEN a terminal action refused with `authorizationRequired` (WPF custom price in v1; simulated refusals in Web tests)
- WHEN the refusal reaches the client
- THEN the wait flow starts instead of a dead-end error

### Requirement: WPF hub lifecycle and thread affinity

The WPF client MUST connect to `/hubs/authorization` authenticated as the current user, MUST marshal UI updates through the dispatcher, MUST dispose the connection and unsubscribe handlers on teardown without leaks, and MUST route the existing custom-price add path (`PosViewModel.Orders.cs`) through the wait flow.

#### Scenario: Hub connection is disposed with the session

- GIVEN a WPF session with the authorization hub connected
- WHEN the session ends or the client closes
- THEN the connection and handlers are disposed
- AND no further hub callbacks reach disposed view models

#### Scenario: UI updates occur on the dispatcher thread

- GIVEN a hub event arriving on a background thread
- WHEN the notification or wait dialog updates
- THEN the update happens through the dispatcher without cross-thread violations
