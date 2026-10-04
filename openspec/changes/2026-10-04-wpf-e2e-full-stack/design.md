# Design: WPF E2E Full-Stack (Level B) & UIA Stability

## Technical Approach

A full-stack fixture composes three concerns: (1) a backend process manager (real `Backend.API` exe against an
isolated `pos_e2e_<suffix>` database, environment-overridden, health-gated); (2) an API bootstrap client that
prepares deterministic state through the real HTTP API (forced admin password rotation after the seeded
`MustChangePassword=true`, exchange rate, test catalog product); (3) client launch reuse — the existing
`WpfAppFixture` launches `Desktop.Client.exe` **without** `--e2e`, with a per-process
`BackendSettings__BaseAddress` environment variable and the persisted client settings file safely neutralized
(backup/restore) so the env fallback applies. New flow tests drive only the UI (sale complete with history
verification; cash drawer session; daily closure) and run gated by the E2E database environment (silent local
absence; fail closed under CI). A bounded UIA retry helper wraps finds/waits (COMException-only) with
configurable timeouts, applied to known flaky spots. CI gains a `wpf-e2e` job mirroring the backend job.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| D1 | Port strategy | Fixture picks a free port (`TcpListener` probe), passes it to the backend via `ASPNETCORE_URLS` and to the client via process-scoped `BackendSettings__BaseAddress` | Fixed 5000 | Avoids collisions with dev servers and parallel runs; deterministic per run. |
| D2 | DB isolation | `E2E_POSTGRES_CONNECTION` base string; fixture derives `pos_e2e_<run>` database, creates/drops it via Npgsql | Reuse `pos_test` | Isolation from the backend suite; teardown leaves no residue. |
| D3 | Gating | Locally without the env: full-stack tests return silently (repo pattern). `GITHUB_ACTIONS=true` without env: fail closed | xUnit `Skip` | Matches the repository's Postgres gating culture; silent-skip would hide CI misconfiguration. |
| D4 | Admin bootstrap | Backend env `SystemSettings__AdminSeedPassword` + `ASPNETCORE_ENVIRONMENT=Development`; bootstrap logs in over HTTP and rotates the forced password before any UI login | Direct DB insert with `MustChangePassword=false` | Exercises the real first-run flow; no schema shortcuts. |
| D5 | Fixture state seed | Real API calls: `POST /api/exchange-rate` (rate), `POST /api/products` (test product name/SKU/price/stock); payment methods and default customer already seeded by the backend | SQL inserts | Keeps bootstrap through the same contracts the app uses; product is searchable by UI flow. |
| D6 | Client settings addressing | Backup the persisted settings → WRITE it with the harness address (`ServerBaseAddress=http://127.0.0.1:<port>/`, `AutoDiscoverOnFailure=false`) → restore the original in teardown | Original D6 (delete + env fallback) | Field-fix 2026-10-04: the env fallback never applies while `Desktop.Client/appsettings.json` pins `localhost:5000` and the persisted value can be non-default; the persisted custom address is the deterministic winner at `App.xaml.cs:157-161` (verified empirically — the client hit the maintainer's dev backend on 5000). |
| D7 | UIA retry policy | `UiaRetry` helpers: catch only `COMException` (transient UIA faults), ≤4 attempts, ~500 ms backoff, surface the last fault; timeout via `E2E_FIND_TIMEOUT_SECONDS` (default 10 s) | xunit-retry packages | Retries must be scoped to transport faults; a whole-test retry would mask real regressions and slow the suite. |
| D8 | Flow automation hooks | Prefer existing `AutomationId`s; add missing ids in Desktop.Client views only where unavoidable (idempotent, no behavior change) | Guessing element names | Keeps tests robust and the product surface untouched except for stable ids. |
| D9 | Cash/closure flow depth | Execute the real session lifecycle discovered during implementation (open → activity → close; daily closure with totals) following the actual business preconditions | Deep arqueo workflows | Level B v1 targets coherent happy paths; deeper closure semantics stay covered by backend tests. |
| D10 | CI job | New blocking `wpf-e2e` job (windows-2025, PostgreSQL preinstalled): build solution, create E2E DB, run the E2E project with env, upload artifacts on failure, `timeout-minutes: 30` | Non-blocking job | The point of the hygiene decision is gating; flakes are mitigated by D7. |
| D11 | Mock mode | `--e2e` mock suite stays untouched and keeps running (fast health/navigation coverage); full-stack tests live in separate classes | Replace mock tests | Two complementary levels: contract/UI-fast (mock) and reality (full-stack). |

## Data Flow

```
xUnit full-stack test
  ▼
FullStackFixture (IClassFixture, collection)
  ├─ env gate (E2E_POSTGRES_CONNECTION; GITHUB_ACTIONS fail-closed)
  ├─ create database pos_e2e_<run> (Npgsql)
  ├─ start Backend.API.exe (ASPNETCORE_URLS=127.0.0.1:<free>, ConnectionStrings__DefaultConnection=<e2e db>,
  │   SystemSettings__AdminSeedPassword=<seed>, ASPNETCORE_ENVIRONMENT=Development)
  ├─ wait /api/health == Healthy
  ├─ bootstrap via HTTP: login(seed) → change-password(rotated) → set rate → create product
  ├─ backup+delete client_settings.json
  ├─ launch Desktop.Client.exe (no --e2e; BackendSettings__BaseAddress=http://127.0.0.1:<free>/)
  ▼
UI flows (FlaUI, UiaRetry-backed): login(rotated) → sale/drawer/closure assertions
  ▼
Teardown: kill client+backend, drop database, restore settings file
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `tests/CommandCenter.Wpf.E2ETests/Fixtures/FullStackFixture.cs` | Create | Backend/DB/client lifecycle + bootstrap orchestration |
| `tests/CommandCenter.Wpf.E2ETests/Fixtures/E2eApiClient.cs` | Create | Fixture-side HTTP bootstrap (login, change-password, rate, product) |
| `tests/CommandCenter.Wpf.E2ETests/Fixtures/UiaRetry.cs` | Create | Bounded COMException retry + configurable timeouts |
| `tests/CommandCenter.Wpf.E2ETests/Fixtures/TestHelper.cs` | Modify | Use UiaRetry; rotated-password login helper |
| `tests/CommandCenter.Wpf.E2ETests/Tests/FullStackSmokeTests.cs` | Create | Real login + POS ready against the real stack |
| `tests/CommandCenter.Wpf.E2ETests/Tests/FullStackSaleTests.cs` | Create | Complete sale + history verification |
| `tests/CommandCenter.Wpf.E2ETests/Tests/FullStackCashClosureTests.cs` | Create | Cash drawer lifecycle + daily closure |
| `tests/CommandCenter.Wpf.E2ETests/Tests/PendingPickupsTests.cs` (+ other flakes) | Modify | Apply UiaRetry to dialog waits |
| `Desktop.Client/Views/*` (only if needed) | Modify | Missing stable AutomationIds for flow automation |
| `.github/workflows/ci.yml` | Modify | `wpf-e2e` job |
| `tests/CommandCenter.Wpf.E2ETests/CommandCenter.Wpf.E2ETests.csproj` | Modify | Npgsql reference (fixture-side DB create/drop) |

## Interfaces / Contracts

```csharp
// Fixture-side bootstrap (real API contracts)
POST /api/auth/login                  { cedula, password } → { token, requiresPasswordChange, user }
POST /api/auth/change-password        { currentPassword, newPassword } (Bearer)
POST /api/exchange-rate               { value } (Bearer, Admin)
POST /api/products                    CreateProductDto (Bearer, Admin)

// Environment contract
E2E_POSTGRES_CONNECTION   // base Npgsql connection string (Host/Username/Password); fixture derives DB name
GITHUB_ACTIONS=true       // fail-closed marker when the E2E env is missing
E2E_FIND_TIMEOUT_SECONDS  // optional UIA timeout override (default 10)
```

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Harness | Boot/health/teardown, gating (silent local, fail-closed CI), settings backup/restore, free-port plumbing | xUnit fixture smoke (gated by `E2E_POSTGRES_CONNECTION`) |
| Flows | Sale complete + history; cash drawer lifecycle; daily closure totals | FlaUI UI automation against the real stack (`UiaRetry`) |
| Stability | Retry helper unit behavior (retries transient, surfaces persistent, never wraps assertions); timeout override | xUnit unit tests with injected fault generators |
| Regression | Existing 18 mock E2E tests keep passing; backend/frontend suites untouched | `dotnet test` (E2E project + main suite) |

## Threat Matrix

Test-only surface: the harness spawns local processes and manages a local test database; environment
variables carry test credentials only (no secrets committed); the client settings file is backed up and
restored; no production code paths change except optional automation ids.

## Migration / Rollout

No schema changes. Additive test infrastructure + CI job; rollback = remove the fixture/tests/job.

## PR Slicing

1. Harness + bootstrap + gating + smoke.
2. UIA stability helper + hardening of existing flaky tests.
3. Real sale flow.
4. Cash drawer + daily closure flows.
5. CI job + docs/ANEXO.

## Open Questions

None. Assumptions flagged: cash/closure happy-path depth depends on real business preconditions (D9); the
seeded admin rotation flow is exercised through the API (D4).
