# custody-partial-delivery Specification

## Purpose

Partial delivery (merchandise in custody) lifecycle for paid pending pickups: per-item delivered/pending quantities, an append-only delivery log, and partial/completed order transitions.

## Requirements

### Requirement: Delivery States

A sale under custody MUST have exactly one logistics state: `PendingPickup` (100% in store), `PartiallyDelivered` (fractional withdrawal happened), or `Delivered` (0% pending). Transition MUST be `PendingPickup → PartiallyDelivered → Delivered`, and a sale MAY complete directly from `PendingPickup` when one event covers all pending quantities. `PickupDate` MUST be set only when the state becomes `Delivered`.

#### Scenario: Full withdrawal in one event
- GIVEN a completed sale in `PendingPickup` with all quantities pending
- WHEN a delivery event covers every pending quantity
- THEN the state becomes `Delivered` and `PickupDate` is set

#### Scenario: Fractional withdrawal
- GIVEN a completed sale in `PendingPickup`
- WHEN a delivery event covers only part of the pending quantities
- THEN the state becomes `PartiallyDelivered`
- AND `PickupDate` remains null

#### Scenario: Pending list includes partial sales
- GIVEN a sale in `PartiallyDelivered` with pending quantities
- WHEN the pending pickups list is queried
- THEN the sale appears with its remaining quantities

### Requirement: Per-Line Quantities

Every `SaleItem` of a custody sale MUST expose a logical breakdown of total quantity, delivered quantity, and pending quantity (delivered = total − pending). The invariant `0 <= DeliveredQuantity <= Quantity` MUST hold at all times, stored `numeric(18,3)`. `DeliveredQuantity` MUST be rebuildable from the delivery log.

#### Scenario: Breakdown after partial events
- GIVEN a sale item with total 10
- WHEN events delivering 4 and then 3 are recorded
- THEN its delivered quantity is 7 and its pending quantity is 3

#### Scenario: Pending list read model
- GIVEN a custody sale with mixed delivered/pending lines
- WHEN the pending pickups list is queried
- THEN each item carries `SaleItemId`, product name, total, delivered, and pending quantities
- AND the sale carries aggregate delivered/total units

### Requirement: Delivery Event Log

Each confirmed withdrawal MUST append one immutable `SaleDelivery` row (sale id, UTC date/time, delivering cashier id and name snapshot, optional notes) with one `SaleDeliveryItem` per product delivered in that event (product id, product name snapshot, quantity). The system MUST NOT expose update or delete paths for these rows, and an event MUST record only the quantities delivered in that event.

#### Scenario: Two events across different days
- GIVEN a sale item with total 10
- WHEN a 6-unit event and later a 4-unit event are recorded
- THEN two `SaleDelivery` rows exist with their own timestamps, cashier, and quantities
- AND each event shows only its own delivered items

#### Scenario: Cashier attribution
- GIVEN a cashier delivers part of an order
- WHEN the delivery is recorded
- THEN the event stores the acting user id and the user name snapshot
- AND later renames of that user do not alter the stored event

### Requirement: Delivery Validation and Atomicity

The system MUST validate server-side (zero-trust): the sale exists, is `Completed`, and is in a custody state (`PendingPickup`/`PartiallyDelivered`); every line belongs to the sale; each requested quantity is greater than zero and MUST NOT exceed the line's pending quantity; no duplicate line ids; at least one positive line. All counter updates, log rows, and the state transition MUST commit atomically or not at all. Two concurrent events on the same sale MUST NOT double-count; the losing transaction MUST fail with `409 Conflict` (ProblemDetails) and no partial writes.

#### Scenario: Over-pending rejected
- GIVEN a line with pending 3
- WHEN a request asks for 4
- THEN the request is rejected with `400` ProblemDetails
- AND no counter, log, or state change is persisted

#### Scenario: Foreign or duplicate line rejected
- GIVEN a sale
- WHEN a request includes a `SaleItemId` of another sale or the same id twice
- THEN the request is rejected with `400` and nothing is persisted

#### Scenario: Concurrent deliveries
- GIVEN two simultaneous events targeting the same pending quantity
- WHEN both transactions commit
- THEN exactly one event is persisted
- AND the other fails with `409` without touching counters or log

### Requirement: Idempotent Delivery Endpoint

`POST /api/sales/{id}/deliveries` MUST require an `Idempotency-Key` header, accept a per-line quantity payload, and return the delivery receipt for the created event. Replaying the same key with the same payload MUST return the original result without creating a second event; the same key with a different payload MUST fail with `422`. Access MUST follow the pickup authorization rules (authenticated, sale-scoped, Driver blocked) and errors MUST use ProblemDetails.

#### Scenario: Replay returns original result
- GIVEN a confirmed delivery with key K
- WHEN the same request with key K is replayed
- THEN the response is the original receipt
- AND no second `SaleDelivery` row is created

#### Scenario: Driver blocked
- GIVEN a Driver user
- WHEN the user calls the deliveries endpoint
- THEN the response is `403` and nothing is persisted

### Requirement: Legacy Confirm Equivalence

The existing `POST /api/sales/{id}/confirm-pickup` MUST keep its response contract and MUST behave as a single delivery event covering all remaining pending quantities, writing the delivery log and per-line counters under the same validations.

#### Scenario: Legacy confirm completes the sale
- GIVEN a partially delivered sale
- WHEN `confirm-pickup` is called
- THEN one event records all remaining pending quantities
- AND the state becomes `Delivered` with `PickupDate` set

#### Scenario: Legacy confirm on non-custody sale
- GIVEN a sale not in a custody state
- WHEN `confirm-pickup` is called
- THEN it fails with the existing invalid-operation error and nothing is persisted

### Requirement: Migration and Backfill

Schema changes MUST be additive: a `DeliveredQuantity` column defaulting to 0 on `SaleItems`, and new `SaleDeliveries`/`SaleDeliveryItems` tables. The migration MUST backfill `DeliveredQuantity = Quantity` for sales already `Delivered` and leave custody sales at 0, without touching financial fields.

#### Scenario: Backfill delivered sales
- GIVEN an existing `Delivered` sale with items
- WHEN the migration runs
- THEN each item has delivered quantity equal to its total quantity

#### Scenario: Backfill custody sales
- GIVEN an existing `PendingPickup` sale
- WHEN the migration runs
- THEN each item keeps delivered quantity 0
