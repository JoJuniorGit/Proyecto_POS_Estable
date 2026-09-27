# closure-orchestration-consolidation Specification

## Purpose

The daily closure MUST have one server-side orchestration path: the service layer owns the
transaction, the effective-rate resolution, the entity assembly and the persistence; controllers
only authorize and delegate. This removes `DbContext` from the touched controllers and brings
`CreateClosure` under the McCabe ceiling.

## Requirements

### Requirement: REQ-COC-01 Single Server-Side Closure Path

Closure orchestration — execution strategy, transaction, effective-rate resolution, closure entity
assembly and persistence — MUST live in `IDailyClosureService`. `DailyClosureController.CreateClosure`
MUST delegate and MUST NOT open a transaction, resolve the rate, or persist.

#### Scenario: Controller delegates the closure run

- GIVEN a valid close request
- WHEN `CreateClosure` is invoked
- THEN it MUST call the closure service and return its result
- AND no persistence call MUST execute inside the controller

#### Scenario: Service owns the transaction

- GIVEN the service executes a closure
- WHEN the run fails mid-way
- THEN the transaction MUST roll back inside the service
- AND no partial closure MUST be persisted

### Requirement: REQ-COC-02 Controllers Authorize and Delegate Only

Touched controllers MUST NOT inject `SalesDbContext` or `InventoryDbContext`, and MUST reach
closure/user data (`DailyClosures`, `Users`) through the service or query layer. RBAC authorization
MUST remain at the controller boundary and MUST NOT move into the service.

#### Scenario: No DbContext in touched controllers

- GIVEN the touched controllers
- WHEN their constructors and fields are inspected
- THEN no `DbContext` type MUST be injected or used

#### Scenario: Authorization still gates the closure

- GIVEN a caller lacking the role required to close a shift or a daily closure
- WHEN the endpoint is invoked
- THEN the response MUST be HTTP 403
- AND no closure MUST be persisted

### Requirement: REQ-COC-03 Complexity Budget Under 10

`DailyClosureController.CreateClosure` MUST measure cyclomatic complexity below 10, and each method
extracted by the change — including `ExecuteClosureCoreAsync` (currently ~12) — MUST also be below 10.

#### Scenario: CreateClosure is under the ceiling

- GIVEN the post-change `CreateClosure`
- WHEN its cyclomatic complexity is measured
- THEN the value MUST be < 10

#### Scenario: Extracted methods are under the ceiling

- GIVEN the methods produced by the decomposition
- WHEN each is measured
- THEN every value MUST be < 10

### Requirement: REQ-COC-04 Behavior Preservation

The refactor MUST NOT change observable closure behavior: response status and payload, persisted
closure amounts, and the already-specified preview rejection
(`DailyClosureController.GetExpectedTotals` 400 on a missing/default `dateUtc`) MUST remain identical.
Persisted closure snapshots MUST NOT be recomputed or rewritten.

#### Scenario: Same inputs, same stored closure

- GIVEN identical close inputs before and after the refactor
- WHEN the closure is created
- THEN the persisted amounts, status labels and response fields MUST be unchanged

#### Scenario: Preview rejection is not regressed

- GIVEN `GET /api/DailyClosure/expected-totals` with `dateUtc` omitted or default
- WHEN the request is handled
- THEN the response MUST remain HTTP 400 with no totals computed
