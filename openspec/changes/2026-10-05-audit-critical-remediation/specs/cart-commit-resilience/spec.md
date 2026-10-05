# Delta for cart-commit-resilience

## ADDED Requirements

### Requirement: Quantity Commit Failures Are Surfaced and Rolled Back

`CommitItemQuantityAsync` and `FlushAllQuantitiesAsync` MUST NOT swallow failures: on error they MUST log via `ClientStateLogger.LogError`, restore the affected item quantities to the server-authoritative values (re-synchronizing from the backend), and notify the operator through `IDialogService` with the exact message `"No se pudo actualizar la cantidad. Se restauró el valor del servidor."` when the rollback succeeds, or the exact message `"No se pudo actualizar la cantidad y no se pudo restaurar el estado. Verifique el carrito antes de cobrar."` when the re-sync also fails. `Debug.WriteLine` MUST NOT be the only handling of a failure.

#### Scenario: Failed commit restores the server value

- GIVEN a cart item whose quantity edit fails on the server
- WHEN the commit task catches the failure
- THEN the item quantity is restored to the server-authoritative value
- AND the failure is logged with the cart origin
- AND the operator sees the rollback error message

#### Scenario: Failed flush rolls back all pending quantities

- GIVEN multiple pending quantity edits and a failing backend
- WHEN the flush task catches the failure
- THEN every affected item is re-synchronized from the server
- AND the failure is logged and notified

#### Scenario: Re-sync failure degrades loudly

- GIVEN a commit failure and an unreachable backend
- WHEN the re-sync also fails
- THEN the failure is logged
- AND the operator is warned that values may be stale
- AND no silent success is reported

### Requirement: No Silent Swallows in CartViewModel

Every catch block in `CartViewModel` MUST log through `ClientStateLogger.LogError` (best-effort recovery persistence included); empty catches are forbidden.

#### Scenario: Recovery-state persistence failure is logged

- GIVEN a failing recovery-state write
- WHEN `PersistRecoveryState` runs
- THEN the failure is logged
- AND the cart flow continues (best-effort semantics preserved)
