# Delta for ci-pipeline

## ADDED Requirements

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
