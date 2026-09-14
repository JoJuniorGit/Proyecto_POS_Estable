# pending-order-hold-lock Specification

## Purpose

Claim/release lifecycle and lock presentation for OnHold sales.

## Requirements

### Requirement: Claim Before Opening an Action Modal

The UI MUST claim a free order before opening its modal: `Checkout` before Cobrar, `Editing` before Editar.

#### Scenario: Claim with the requested action

- GIVEN two free pending orders
- WHEN Cobrar is triggered, then Editar on the other
- THEN `claimSale` receives `'Checkout'` and `'Editing'` respectively
- AND each modal opens after its claim resolves

### Requirement: Claim Conflict (409)

A claim rejected because another cashier holds the lock MUST NOT open the modal, MUST show the server message in the error banner, and SHOULD reload the list. A rejection without a message SHALL fall back to `No se pudo reclamar el pedido.`

#### Scenario: Another cashier won the race

- GIVEN an order locked by another cashier on the server
- WHEN a claim is attempted and the API responds 409 with a `message`
- THEN no modal opens, the banner shows that message, and the list reloads

### Requirement: Best-Effort Lock Release

On close without completing, on completion and on cancellation, the UI MUST release the caller's own active lock best-effort; a failed or no-op release MUST NOT surface an error.

#### Scenario: Closing a modal

- GIVEN the caller holds the active lock
- WHEN the checkout or edit modal closes without completing
- THEN `releaseSale(activeId)` runs, clearing the lock and reloading the list

#### Scenario: Completion or cancellation

- GIVEN the caller holds the active lock
- WHEN full payment, a partial payment, or cancellation succeeds
- THEN the active lock is released with no user-visible error

#### Scenario: Release failure is swallowed

- GIVEN a release for an already-completed sale
- WHEN it fails or no-ops
- THEN the UI stays usable with no error banner

### Requirement: Foreign Lock Presentation and Protection

For another cashier's `claimedByUserId`, Cobrar and Editar MUST be disabled with the lock label as title, the badge MUST show `Bloqueado por <nombre> - <acción>`, and that lock MUST NOT be released except via force-release.

#### Scenario: Locked by another cashier

- GIVEN an order claimed by another user with action `Checkout`
- WHEN the row or card renders
- THEN the badge shows `Bloqueado por <nombre> - En proceso de pago`
- AND Cobrar and Editar are disabled with it as title

#### Scenario: Locked by me or free

- GIVEN an order claimed by me, or one with no claim
- WHEN the row or card renders
- THEN the badge shows `Bloqueado por ti`, or nothing when free
- AND Cobrar and Editar stay enabled

### Requirement: Elevated Force-Release

Force-release MUST be available only to `Admin` and `Manager`, and only for a foreign lock; it MUST release with force and reload the list.

#### Scenario: Admin releases a foreign lock

- GIVEN an Admin viewing an order locked by another cashier
- WHEN Liberar is triggered
- THEN `releaseSale(order.id, true)` is called and the list reloads

#### Scenario: Cashier cannot force-release

- GIVEN a Cashier viewing an order locked by another cashier
- WHEN the row or card renders
- THEN no "Liberar" control is rendered

### Requirement: Testable Controller with Injected Dependencies

The lifecycle MUST live in a pure controller built by `createHoldOrderLockController({ claimSale, releaseSale, reload, onError })` exposing `start`, `releaseActive`, `forceRelease` and `getActiveLockId()`. It MUST NOT depend on React or the DOM, and `PendingOrdersPage` MUST preserve observable behavior while delegating to it.

#### Scenario: Lifecycle runs headlessly

- GIVEN injected spies for claim, release, reload and error
- WHEN `start`, `releaseActive` and `forceRelease` are invoked
- THEN each spy receives the expected arguments
- AND no DOM or React is required

#### Scenario: Guard blocks a foreign lock

- GIVEN an order locked by another user
- WHEN `start(sale, 'Checkout', currentUserId)` is called
- THEN `claimSale` is never invoked
