# ci-pipeline Specification

## Purpose

Continuous integration MUST run green on push/PR to `main`, `develop` and `V0.15`: the backend job
executes the Windows-only .NET test project against a freshly created database, and the frontend job
installs and tests under npm 11.

## Requirements

### Requirement: Backend Job Runs on a Windows Runner

The backend job MUST run on a Windows runner (`windows-2025`) so the `net10.0-windows` testhost
resolves `Microsoft.WindowsDesktop.App`. It MUST NOT execute the .NET test project on a Linux runner.

#### Scenario: Windows-only test project executes

- GIVEN the backend job is configured
- WHEN the job runs on its declared runner
- THEN `dotnet test` starts the testhost without a missing `Microsoft.WindowsDesktop.App` abort
- AND test results and a coverage report are produced

#### Scenario: Linux runner regression is rejected

- GIVEN the workflow file
- WHEN the backend job's `runs-on` is inspected
- THEN it is a Windows image and never `ubuntu-latest`

### Requirement: PostgreSQL Enabled and Started Before Tests

The backend job MUST set the runner's PostgreSQL service startup type to Manual before starting it,
and MUST poll for readiness before running tests.

#### Scenario: Disabled service is enabled then started

- GIVEN the runner image ships PostgreSQL with a Disabled startup type and Stopped status
- WHEN the database preparation step runs
- THEN the startup type is set before `Start-Service`
- AND the step does not throw under `$ErrorActionPreference = 'Stop'`

#### Scenario: Readiness polled before tests

- GIVEN the service has been started
- WHEN the readiness poll runs
- THEN it waits until the server accepts a connection
- AND no test starts before readiness

### Requirement: Fresh Test Database Every Run

The job MUST drop and recreate `pos_test` before tests, so the suite always exercises a fresh database.

#### Scenario: Pre-existing database is replaced

- GIVEN `pos_test` already exists on the runner
- WHEN the preparation step runs
- THEN `pos_test` is dropped with forced connections and recreated
- AND the test step connects to an empty `pos_test`

### Requirement: Coverage Gate and Vulnerability Audit Under PowerShell 7

Both steps MUST run under PowerShell 7 with behavior equivalent to the previous bash versions: the
coverage gate MUST fail with an actionable error when no coverage report exists, and the audit MUST
fail when a vulnerable package is detected.

#### Scenario: Missing coverage report fails actionably

- GIVEN no `coverage.cobertura.xml` was produced
- WHEN the coverage gate runs
- THEN the step fails with a message naming the missing report

#### Scenario: Vulnerable package fails the audit

- GIVEN the audit output reports a known vulnerability
- WHEN the audit step runs
- THEN the step fails

### Requirement: Frontend Job on Node 24

The frontend job MUST use Node 24 (npm 11), and `Web.Frontend/package.json` MUST declare
`engines.node >= 24`.

#### Scenario: npm ci accepts the versioned lockfile

- GIVEN the lockfile containing optional `@emnapi/*` entries
- WHEN `npm ci` runs under the pinned Node version
- THEN installation succeeds

#### Scenario: Declared engine range excludes npm 10

- GIVEN the `engines.node` range
- WHEN it is evaluated against Node 22
- THEN the range does not include that version

### Requirement: Job Timeout and Smoke Database Isolation

The backend job timeout MUST accommodate the Windows restore, build, test and coverage run, and smoke
tests MUST receive a per-run database suffix.

#### Scenario: Timeout covers the full Windows run

- GIVEN restore, Release build, 970+ tests and coverage instrumentation on Windows
- WHEN the job's timeout is configured
- THEN the run can complete without truncation

#### Scenario: Per-run smoke database suffix

- GIVEN the test step sets a smoke database suffix
- WHEN smoke tests create `pos_smoke_<suffix>` and `pos_zero_<suffix>`
- THEN the suffix is unique per run

### Requirement: Desktop E2E Job in CI

The pipeline MUST include a blocking desktop E2E job running on the Windows runner with PostgreSQL
available: it MUST build the full solution, create an isolated E2E database, run the WPF E2E suite with the
E2E database environment configured, and upload test artifacts when it fails. The job MUST NOT run E2E in a
silent-skip state: with the CI environment detected and the database configured, full-stack tests MUST
execute for real.

#### Scenario: E2E job executes full-stack flows

- GIVEN a push or pull request triggering the pipeline
- WHEN the desktop E2E job runs
- THEN the WPF E2E suite executes against a real backend process and the isolated CI database
- AND the job result reflects the suite's real outcome

#### Scenario: Failure artifacts

- GIVEN the desktop E2E job fails
- WHEN the job finishes
- THEN test results/artifacts are uploaded for diagnosis
