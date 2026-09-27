# closure-orchestration-consolidation Specification - Delta

## Purpose

Closes `S3-06` and `S3-07`: the 650-line `DailyClosureService` must be split into cohesive files
within the 300-500 line ceiling with every method under McCabe 10, and the legacy public
entity-returning `CreateClosureAsync(DailyClosure)` entry point - a divergent, transaction-free
copy of the closure rules - must be removed or reduced to a private adapter delegating to the
command path.

Delta mapping against `openspec/specs/closure-orchestration-consolidation/spec.md`:

| ID | Existing requirement | Delta action |
|----|----------------------|--------------|
| REQ-COC-01 | Single Server-Side Closure Path | Unchanged |
| REQ-COC-02 | Controllers Authorize and Delegate Only | Unchanged |
| REQ-COC-03 | Complexity Budget Under 10 | MODIFIED - ceiling extended to the closure service |
| REQ-COC-04 | Behavior Preservation | Unchanged |
| REQ-COC-05 | - | ADDED - single closure-rule implementation |
| REQ-COC-06 | - | ADDED - closure service file cohesion budget |

## MODIFIED Requirements

### Requirement: REQ-COC-03 Complexity Budget Under 10

`DailyClosureController.CreateClosure` MUST measure cyclomatic complexity below 10, and every method
of `DailyClosureService` and of each partial or extracted sub-service it becomes - including
`ExecuteClosureCoreAsync` - MUST also be below 10.
(Previously: the ceiling was scoped to `CreateClosure` and the methods extracted by that slice.)

#### Scenario: CreateClosure is under the ceiling

- GIVEN the post-change `CreateClosure`
- WHEN its cyclomatic complexity is measured
- THEN the value MUST be < 10

#### Scenario: Closure service methods are under the ceiling

- GIVEN the post-change `DailyClosureService` and its partials
- WHEN each method is measured
- THEN every value MUST be < 10

## ADDED Requirements

### Requirement: REQ-COC-05 Single Closure-Rule Implementation

Closure rules (validation, expected-total resolution, line building, totals and persistence) MUST
have exactly one implementation. The public entity-returning entry point
`DailyClosureService.CreateClosureAsync(DailyClosure)` MUST be removed, or reduced to a non-public
adapter that delegates to `CreateClosureFromCommandAsync`; no second, divergent copy of the closure
rules MAY remain reachable.

#### Scenario: No public entity entry point

- GIVEN the public surface of `DailyClosureService`
- WHEN it is inspected
- THEN no public method MAY accept or return `DailyClosure` to create a closure

#### Scenario: Any remaining legacy seam delegates

- GIVEN a closure created through any remaining entry point
- WHEN the closure rules execute
- THEN they MUST run the single implementation
- AND no divergent rule copy MUST be reachable

### Requirement: REQ-COC-06 Closure Service File Cohesion Budget

`DailyClosureService` MUST be split so every resulting file is at most 500 lines, and the split MUST
be by cohesive responsibility (orchestration, line building, totals) so each file has one reason to
change.

#### Scenario: Each file is within the ceiling

- GIVEN the post-change `DailyClosureService` files
- WHEN each file's line count is measured
- THEN every file MUST be <= 500 lines

#### Scenario: The split is by responsibility

- GIVEN the resulting partial or sub-service files
- WHEN their responsibilities are inspected
- THEN each file MUST own a single closure responsibility
- AND no rule logic MAY be duplicated across them
