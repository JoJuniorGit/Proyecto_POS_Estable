# Tasks: WPF E2E Full-Stack (Level B) & UIA Stability

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~1,200–1,800 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 5 chained work units |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached from the V0.15 chain) |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Harness + bootstrap + gating + smoke | `dotnet test tests/CommandCenter.Wpf.E2ETests/CommandCenter.Wpf.E2ETests.csproj -c Release --filter "FullyQualifiedName~FullStackSmoke"` | Remove fixture files; env gate off |
| 2. UIA stability + flake hardening | same project `--filter "FullyQualifiedName~PendingPickups|FullyQualifiedName~LoginView"` | Revert UiaRetry usage |
| 3. Real sale flow | `--filter "FullyQualifiedName~FullStackSale"` | Revert sale tests |
| 4. Cash drawer + daily closure | `--filter "FullyQualifiedName~FullStackCashClosure"` | Revert closure tests |
| 5. CI job + docs | pipeline run on the branch | Remove `wpf-e2e` job |

## Phase 1: Full-Stack Harness

- [x] 1.1 `Fixtures/FullStackFixture.cs`: env gate (silent local / fail-closed CI), free-port probe, database create/drop (`pos_e2e_<run>` via Npgsql), backend process launch (`ASPNETCORE_URLS`, `ConnectionStrings__DefaultConnection`, `SystemSettings__AdminSeedPassword`, `ASPNETCORE_ENVIRONMENT=Development`), health wait, settings backup/delete/restore, client launch without `--e2e` with `BackendSettings__BaseAddress` (reuse `WpfAppFixture` mechanics), teardown in `finally` (idempotent).
- [x] 1.2 `Fixtures/E2eApiClient.cs`: real API bootstrap — login (seed), change-password (rotated constant for the suite), set exchange rate, create the test product; clear failures when any call fails.
- [x] 1.3 `CommandCenter.Wpf.E2ETests.csproj`: `Npgsql` reference for fixture-side DB management.
- [x] 1.4 `FullStackSmokeTests.cs`: boot + real login + POS ready (search input visible) + no error dialogs; teardown leaves no `pos_e2e_*` database and restores settings (asserts in-test).
- [x] 1.5 Gating tests: without env → silent return locally; with `GITHUB_ACTIONS=true` and no env → throw (unit-testable helper).

## Phase 2: UIA Stability (Higiene)

- [x] 2.1 `Fixtures/UiaRetry.cs`: bounded retry (≤4 attempts, ~500 ms backoff) for transient `COMException` only; `E2E_FIND_TIMEOUT_SECONDS` env override (default 10 s); never wraps assertions; unit tests with injected fault generators (retries transient, surfaces persistent).
- [x] 2.2 Apply `UiaRetry` to `TestHelper` waits and to the known flaky dialog checks (`PendingPickupsTests` dialog-closed/modal-open waits; health navigation optional); keep behavior identical on success paths.
- [x] 2.3 Repeat-run evidence: the previously flaky class passes ≥3 consecutive runs.

## Phase 3: Real Sale Flow

- [x] 3.1 `FullStackSaleTests.cs`: real login → search fixture product → add to cart → checkout → cash payment → sale completes with invoice number → visible in Sales History with coherent totals.
- [x] 3.2 Add missing stable `AutomationId`s in Desktop.Client views only where unavoidable (no behavior change).
- [x] 3.3 Assert no HTTP mock participates (full-stack fixture only).

## Phase 4: Cash Drawer + Daily Closure

- [x] 4.1 Discover real preconditions (drawer open state machine, closure requirements) from the VMs/APIs.
- [x] 4.2 `FullStackCashClosureTests.cs`: open drawer session → cash activity → close session; execute the daily closure from the UI verifying state/totals against the registered activity.
- [x] 4.3 Keep the happy path bounded; deeper arqueo semantics remain backend-test territory.

## Phase 5: CI + Closure

- [x] 5.1 `.github/workflows/ci.yml`: blocking `wpf-e2e` job (windows-2025): checkout, .NET setup, restore/build, start PostgreSQL only (the fixture derives/creates/drops its own `pos_e2e_<run>` database per D2 — no `pos_test`/`pos_e2e_ci` creation), run the E2E project with `E2E_POSTGRES_CONNECTION` (+ `GITHUB_ACTIONS` implied), upload artifacts on failure, `timeout-minutes: 30`.
- [x] 5.2 `dotnet build CommandCenter.slnx -c Release` 0/0; full main suite green; E2E suite green locally (mock + full-stack with env).
- [x] 5.3 Independent verification per slice + final; ANEXO 8.148 in `docs/reporte.txt`; note gating behavior and rollback.
