# api-dto-boundary Specification

## Purpose

No EF entity crosses the API boundary for the drawer and closure contracts. Immutable DTOs replace
`DailyClosure`/`ClosureDetail`/`CashDrawerSession`/`CashTransaction` in responses and in service
signatures, and entity instances are no longer fabricated as projections.

## Requirements

### Requirement: REQ-ADB-01 Closure Contracts Return DTOs

`DailyClosureController.GetClosure`, `DailyClosureController.CreateClosure` and the
`IDailyClosureService` closure read/create methods MUST return immutable DTOs and MUST NOT return or
serialize `DailyClosure` or `ClosureDetail`.

#### Scenario: Closure response carries no EF entity

- GIVEN a persisted closure
- WHEN `GET` returns it
- THEN the body MUST be the declared closure DTO
- AND it MUST NOT contain EF navigation members or entity-only properties

#### Scenario: Service returns a DTO

- GIVEN the closure service called directly
- WHEN `CreateClosureAsync`/`GetClosureAsync` complete
- THEN the returned values MUST be the DTO types, not entity types

### Requirement: REQ-ADB-02 Drawer Contracts Return DTOs

`CashDrawerController` actions (`GetActiveSession`, `OpenSession`, `CloseSession`, `AddTransaction`)
and the public `ICashDrawerService` methods MUST return immutable DTOs.
`CashAdvanceResultDto.ExpenseTransaction`/`IncomeTransaction` MUST be DTOs, not `CashTransaction`.

#### Scenario: Drawer session response is a DTO

- GIVEN an active drawer session
- WHEN any of the touched drawer actions responds
- THEN the body MUST be the declared DTO
- AND MUST NOT be a `CashDrawerSession` or `CashTransaction`

#### Scenario: Cash advance result exposes DTOs

- GIVEN a cash advance registered through the API
- WHEN the request completes
- THEN both transaction members of the result MUST be DTO instances

### Requirement: REQ-ADB-03 Projection Without Entity Instantiation

`GetHistoryAsync` MUST produce its result through a DTO projection and MUST NOT materialize entity
instances outside tracking (e.g. `new CashTransaction { Sale = new Sale { ... } }`).

#### Scenario: History is projected, not hand-built

- GIVEN drawer transactions with related sales
- WHEN history is requested
- THEN every item MUST be a DTO
- AND no `CashTransaction` instance MUST be created for the response

### Requirement: REQ-ADB-04 DTO Immutability and Field Parity

The new drawer/closure DTOs MUST be constructed only from a projection source and MUST NOT expose
settable properties (init-only or record semantics). Each DTO MUST carry every field the WPF and Web
clients consume today, under the same JSON names, so the swap does not drop data or break bindings.

#### Scenario: DTOs expose no public setter

- GIVEN a DTO type in the drawer/closure contract
- WHEN its public members are inspected
- THEN it MUST NOT expose a settable property that allows post-construction mutation

#### Scenario: Existing client fields survive the swap

- GIVEN the fields the WPF and Web clients bind today
- WHEN the DTO replaces the entity in the response
- THEN each of those fields MUST still be present with the same JSON name and value
