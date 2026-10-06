# Delta for api-idempotency

## ADDED Requirements

### Requirement: Idempotency Key on Sale Item Mutations

POST `/api/sales/{id}/items` MUST require the `Idempotency-Key` header (400 with the missing-key message when absent), MUST replay the stored response for a repeated key with the same payload (header `X-Cache-Lookup: HIT`), and MUST reject a reused key with a different payload with 422. A replayed request MUST NOT add the item again.

#### Scenario: Retry after a network microcut does not duplicate the item

- GIVEN an AddItem request that succeeded but whose response was lost
- WHEN the client retries with the same key and payload
- THEN the stored response is replayed
- AND the item quantity is unchanged by the replay

#### Scenario: Missing key fails closed

- GIVEN no `Idempotency-Key` header
- WHEN POST items runs
- THEN it returns 400 with the missing-key message

### Requirement: Idempotency Key on Cash Transactions

POST `/api/cashdrawer/transaction` MUST require the `Idempotency-Key` header and MUST NOT register a second transaction for a replayed key.

#### Scenario: Retried expense is not duplicated

- GIVEN a cash transaction that succeeded server-side but whose response was lost
- WHEN the client retries with the same key
- THEN the stored response is replayed
- AND the cash ledger shows exactly one transaction

### Requirement: Idempotency Key and Duplicate Guard on Daily Closure

POST `/api/dailyclosure` MUST require the `Idempotency-Key` header, MUST NOT create a second closure for a replayed key, and MUST reject creating a second closure for the same `ClosureDate` with an explicit conflict error instead of persisting a phantom closure.

#### Scenario: Retried closure does not create a phantom

- GIVEN a closure that succeeded and a retry with the same key
- WHEN the retry runs
- THEN the stored response is replayed
- AND exactly one closure exists for that date

#### Scenario: Second closure for the same date is rejected

- GIVEN an existing closure for date D
- WHEN a new closure for date D arrives with a fresh key
- THEN the request fails with the explicit conflict error
- AND no second closure is persisted

### Requirement: Idempotent Sale Cancellation

POST `/api/sales/{id}/cancel` MUST be idempotent: cancelling an already-cancelled sale MUST return a success response reflecting the cancelled state, not a 409 conflict.

#### Scenario: Repeated cancellation is a no-op

- GIVEN an already-cancelled sale
- WHEN cancel is called again
- THEN the response is a success reflecting the cancelled state
- AND no second cancellation side effect occurs
