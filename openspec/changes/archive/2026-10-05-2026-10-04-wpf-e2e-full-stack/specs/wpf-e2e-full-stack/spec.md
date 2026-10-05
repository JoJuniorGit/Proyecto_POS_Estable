# Delta for wpf-e2e-full-stack

## ADDED Requirements

### Requirement: Full-Stack Harness Lifecycle

The E2E project MUST provide a fixture that, per test run: selects a free local port; starts the real
`Backend.API` against an isolated PostgreSQL database (`pos_e2e_<suffix>`); waits until the anonymous health
endpoint reports healthy; bootstraps state through the real API (rotates the forced admin password, sets the
exchange rate, creates a test catalog product); neutralizes the client's persisted server settings safely
(backup before, restore after); launches the real WPF client **without** `--e2e` pointed at the harness backend
via configuration/environment; and tears everything down (client and backend processes, database drop,
settings restore) even on failures. Full-stack tests MUST return silently when the E2E database environment is
absent locally, and MUST fail closed when the CI environment is detected.

#### Scenario: Harness boot and clean teardown

- GIVEN a local PostgreSQL reachable with the E2E environment variable set
- WHEN a full-stack test starts
- THEN the backend answers healthy on the fixture's port against an isolated database
- AND the client runs against that backend without `--e2e`
- AND after the test the database is dropped, processes are stopped, and the client settings file is restored to its previous content

#### Scenario: Gated without environment

- GIVEN no E2E database environment variable locally
- WHEN the full-stack suite runs
- THEN full-stack tests return silently without failing
- AND the mock-based E2E tests still run normally

#### Scenario: Fail closed in CI without database

- GIVEN the CI environment and no E2E database environment variable
- WHEN the full-stack suite runs
- THEN the suite MUST fail with a clear message instead of skipping silently

### Requirement: Real Sale Flow

The suite MUST execute a complete point-of-sale sale through the real stack and the real UI: authenticate with
the bootstrapped admin credentials, search the fixture-created product, add it to the cart, open checkout,
register a cash payment, and complete the sale obtaining an invoice number; the sale MUST then be verifiable
through the Sales History view. No HTTP mock may participate in this flow.

#### Scenario: Complete sale end to end

- GIVEN the full-stack harness is running with the test product seeded
- WHEN the operator logs in and performs the complete sale flow
- THEN the sale completes with an invoice number shown by the UI
- AND the sale appears in Sales History with matching totals

#### Scenario: No mocks in the flow

- GIVEN the full-stack harness client
- WHEN the sale flow executes
- THEN every request goes to the real backend process and database
- AND the `--e2e` mock handler is not active

### Requirement: Real Cash Drawer and Daily Closure Flows

The suite MUST exercise the real cash drawer and daily closure functions through the UI against the real stack:
open the drawer session, register cash activity, close the session, and execute the daily closure verifying
that the resulting state and totals are coherent with the operations performed.

#### Scenario: Cash drawer session lifecycle

- GIVEN the full-stack harness is running
- WHEN the operator opens the drawer, performs cash activity, and closes the session
- THEN each step reflects the real server state in the UI

#### Scenario: Daily closure reflects real totals

- GIVEN cash activity registered through the real stack
- WHEN the daily closure is executed from the UI
- THEN the closure state and totals match the registered activity
