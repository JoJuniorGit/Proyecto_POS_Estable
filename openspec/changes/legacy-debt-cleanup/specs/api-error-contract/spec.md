# api-error-contract Specification

## Purpose

Every error surfaced by the touched endpoints uses the repo's RFC 7807 helpers
(`Backend.API/Controllers/ApiProblemResults.cs`), so clients receive one payload shape and no
anonymous error object or `message`/`Message` casing drift remains. Also covers the two
contract-visible items grouped here by the proposal: silent `catch` removal (item 12) and the
dead `CloseShiftRequest` fields (item 35).

## Requirements

### Requirement: REQ-AEC-01 RFC 7807 Payload Shape

All error responses of the touched endpoints (`ShiftsController`, `CashDrawerController.AddTransaction`,
`DailyClosureController` legacy sites) MUST be RFC 7807 ProblemDetails payloads with a `status`
member matching the HTTP status, and MUST NOT be built from anonymous objects. Sites already using
`Problem(...)` (e.g. the slice-1 preview 400) MUST keep their current status and semantics.

#### Scenario: Error response is ProblemDetails

- GIVEN a request that a touched endpoint rejects
- WHEN the rejection is produced
- THEN the body MUST be an RFC 7807 payload whose `status` equals the HTTP status
- AND the member naming MUST match the other ProblemDetails responses in the API

#### Scenario: No anonymous error object remains

- GIVEN the touched endpoints
- WHEN their error sites are inspected
- THEN no error response MUST be constructed from an anonymous object

### Requirement: REQ-AEC-02 Helper and Status Fidelity

Error sites MUST be expressed through the existing `ApiProblemResults` helpers (`ApiBadRequest`,
`ApiForbidden`, `ApiNotFound`, `ApiConflict`, `ApiUnprocessableEntity`) using the status that matches
the failure. HTTP status codes MUST be preserved by the recomposition.

#### Scenario: RBAC denial maps to forbidden

- GIVEN a caller lacking the role the touched endpoint requires
- WHEN the endpoint denies the request
- THEN the response MUST be HTTP 403 produced by `ApiForbidden`
- AND the body MUST be an RFC 7807 payload

#### Scenario: Invalid input maps to bad request

- GIVEN a request the touched endpoint cannot accept
- WHEN validation rejects it
- THEN the response MUST be HTTP 400 produced by `ApiBadRequest`

### Requirement: REQ-AEC-03 No Silent Failure in Closure Receipt Writers

The closure receipt retry helpers (`TryWriteFileWithRetry`, `TryWriteTextWithRetry`) MUST log every
swallowed failure through structured logging, MUST NOT contain an empty `catch`, and MUST NOT block
an async path with `Thread.Sleep`. Retry attempts and multi-path write semantics MUST be unchanged.

#### Scenario: Write failure is logged, not swallowed

- GIVEN a receipt write that fails on every attempt
- WHEN the helper exhausts its attempts
- THEN a log entry MUST identify the target path and the failure
- AND the closure result MUST still be returned

#### Scenario: Retry wait does not block the thread

- GIVEN a receipt write that fails and is retried
- WHEN the wait between attempts is applied
- THEN the wait MUST be asynchronous

### Requirement: REQ-AEC-04 Dead CloseShiftRequest Fields Removed

`CloseShiftRequest` MUST NOT declare `CashierName` or `CashierCedula`; the close contract MUST NOT
accept them (the cashier identity comes from the authenticated principal). All known senders
(Web `shiftApi.js`, WPF view models) MUST stop sending them in the same slice.

#### Scenario: Close request contract has no discarded fields

- GIVEN the `POST /api/shifts/close` request contract
- WHEN it is inspected
- THEN it MUST NOT declare `CashierName` or `CashierCedula`

#### Scenario: Legacy sender does not break the close

- GIVEN a sender that still posts `cashierName`/`cashierCedula`
- WHEN the request body is bound
- THEN the close MUST still succeed, with the extra members ignored

#### Scenario: Closure ownership is derived server-side

- GIVEN an authenticated close request
- WHEN the closure is persisted
- THEN its cashier identity MUST come from the authenticated principal
