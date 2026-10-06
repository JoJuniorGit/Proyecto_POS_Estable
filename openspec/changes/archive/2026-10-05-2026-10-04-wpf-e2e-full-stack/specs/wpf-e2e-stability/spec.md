# Delta for wpf-e2e-stability

## ADDED Requirements

### Requirement: Bounded UIA Retry Policy

The E2E suite MUST use find/wait helpers that retry ONLY on transient UI-automation faults (UI Automation
`COMException` timeouts), with bounded attempts and small backoff; retries MUST NOT wrap or mask assertion
outcomes, and the definitive failure MUST surface the last observed fault. Find timeouts MUST be configurable
through an environment variable with a documented default. The policy MUST be applied to the known flaky
spots (dialog-closed and modal-open waits) and to the new full-stack flow helpers.

#### Scenario: Transient COM fault recovers

- GIVEN a UI-automation find that throws a transient COM timeout on the first attempt
- WHEN the retry helper executes
- THEN the find is retried within the bounded attempts
- AND the test continues normally when a later attempt succeeds

#### Scenario: Persistent fault surfaces

- GIVEN a find that keeps throwing transient COM faults beyond the attempt budget
- WHEN the retry helper exhausts its attempts
- THEN the last fault is surfaced to the test
- AND the test fails with that evidence

#### Scenario: Assertions are never retried

- GIVEN an assertion about a value already obtained from the UI
- WHEN the assertion fails
- THEN no retry mechanism re-evaluates or masks it

#### Scenario: Configurable timeout

- GIVEN the timeout environment variable set to a custom value
- WHEN find/wait helpers execute
- THEN they honor the configured timeout instead of the default
