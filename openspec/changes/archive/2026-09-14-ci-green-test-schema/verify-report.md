```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:1efee5f6f26b3630168c7895ac886b6a2b7069d36cc849ccfe8645adef4bc33e
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 16/16
scenarios: 28/28
test_command: "dotnet test CommandCenter.slnx -c Release --no-build --nologo (fresh pos_test; TEST_POSTGRES_CONNECTION set)"
test_exit_code: 0
test_output_hash: sha256:7b331bb6b1e239c3712b53b344af2313c1b31357653c86a84f27e6058f2c1e60
build_command: "dotnet build CommandCenter.slnx -c Release --nologo"
build_exit_code: 0
build_output_hash: sha256:15638e66ccd80ce4985f014c82c8e306678476e8d0c621c34cf86ddfd58abe2b
```

# Verify Report: ci-green-test-schema

| Field | Value |
|-------|-------|
| Change | `ci-green-test-schema` |
| Verdict | **pass_with_warnings** |
| Verifier | sdd-verify (independent; apply claims not trusted) |
| Store | openspec |
| Strict TDD | disabled |
| Authoritative spec counts | 16 requirements / 28 scenarios |
| Evidence revision | `sha256:1efee5f6f26b3630168c7895ac886b6a2b7069d36cc849ccfe8645adef4bc33e` (git HEAD `2f8a40d`) |
| Environment | Windows 11, PowerShell 7, .NET 10 SDK, PostgreSQL 18, Node v24.11.1 / npm 11.6.2 |

## Check Results (verbatim)

| # | Command | Observed result |
|---|---------|-----------------|
| 1 | `dotnet build CommandCenter.slnx -c Release --nologo` | `0 Advertencia(s)` / `0 Errores` — `BUILD_EXIT=0` (hash `sha256:15638e66...be2b`) |
| 2 | Fresh DB: drop/create `pos_test` + `dotnet test CommandCenter.slnx -c Release --no-build --nologo` | `Con error: 0, Superado: 970, Omitido: 0, Total: 970` — `DOTNET_TEST_EXIT=0` (first run on empty DB) |
| 3 | Repeat `dotnet test` on same `pos_test` | `Con error: 0, Superado: 970, Omitido: 0, Total: 970` — exit 0 (idempotent) |
| 4 | Coverage gate with `TEST_POSTGRES_CONNECTION` set (no silent-pass) | Tests 970/970; `python scripts/check-coverage.py <newest>` → `Core 0.8333 [OK]`, `Sales.Module 0.8911 [OK]`, `Inventory.Module 0.8214 [OK]`; `GATE_EXIT=0` |
| 5 | Half-initialized repair: `pos_verify_repair` with only `Products`, then `--filter "FullyQualifiedName~PostgresRealConnection"` | Pre-tables: `Products`. Post: `Products`, `Users`. Test `1/1` passed; `REPAIR_TEST_EXIT=0`; DB dropped |
| 6 | `npm ci` / `npm run lint` / `npm test` (workdir `Web.Frontend`) | `npm ci`: added 50 packages, 0 vulnerabilities, exit 0. `LINT_EXIT=0`. `tests 178 / pass 178 / fail 0 / skipped 0` — `TEST_EXIT=0` |
| 7 | Static CI inspection of `.github/workflows/ci.yml` | All required constructs present (see ci-pipeline table). Actual GitHub Actions execution NOT verifiable locally |
| 8 | Drift/protection: `git status --porcelain`, `git diff --name-only` | Only expected change files + documented other-session WIP; NO path under product projects; no migration; `DatabaseInitializer.cs` untouched |

Raw counts for check 2/3/4: **970 passed, 0 failed, 0 skipped** on each run. Coverage layers: Core 0.8333 (min 0.70), Sales.Module 0.8911 (min 0.80), Inventory.Module 0.8214 (min 0.72).

### Check 8 — drift detail

- Working tree contains only: modified `.github/workflows/ci.yml`, `CommandCenter.Tests/Builders/TestDatabaseFactory.cs`, `CommandCenter.Tests/Integration/DailyClosureRetryIntegrationTests.cs`, `CommandCenter.Tests/Integration/HistoryImmutabilityTests.cs`, `CommandCenter.Tests/Unit/SalesServiceUnitTests.cs`, `Web.Frontend/package.json`; untracked `CommandCenter.Tests/Builders/TestSchemaBootstrap.cs`, `openspec/changes/ci-green-test-schema/`; plus the pre-existing other-session WIP (`.opencode/agent/reviewer.md`, `AGENTS.md`, `ARCHITECTURE.md` deleted, `Web.Frontend/README.md`, `docs/INSTALLATION.md`, `docs/reporte.txt`, `architecture-*.md`, `docs/INSTALLATION-advanced.md`).
- `git diff --name-only -- Core Sales.Module Inventory.Module Backend.API Desktop.Client Desktop.Client.Core Logistics.Module UpdaterService` → empty (product code unchanged).
- No `Migrations` path in the diff. `Backend.API/Startup/DatabaseInitializer.cs` shows no status entry (untouched).
- Do-not-touch files `CommandCenter.Tests/coverage.runsettings`, `scripts/check-coverage.py`, `CommandCenter.Tests/CommandCenter.Tests.csproj`, `Directory.Build.props`, `ConcurrencyCapacityTests.cs`, `FaultToleranceTests.cs`, `WebApplicationFactorySmokeTests.cs` are absent from `git status` (unchanged).

## Requirement-by-Requirement Conformance

### Spec: ci-pipeline (6 requirements / 11 scenarios)

| Requirement | Scenario | Result | Evidence |
|---|---|---|---|
| Backend Job Runs on a Windows Runner | Windows-only test project executes | PASS | `runs-on: windows-2025` (ci.yml L20); local Windows `dotnet test` started the `net10.0-windows` testhost and produced 970/970 + `coverage.cobertura.xml` |
| Backend Job Runs on a Windows Runner | Linux runner regression is rejected | PASS | Backend `runs-on` is `windows-2025`, never `ubuntu-latest` (ci.yml L20) |
| PostgreSQL Enabled and Started Before Tests | Disabled service is enabled then started | PASS (static); runtime NOT-VERIFIABLE-LOCALLY | `Set-Service -Name $service.Name -StartupType Manual` (L51) precedes `Start-Service -Name $service.Name` (L52) |
| PostgreSQL Enabled and Started Before Tests | Readiness polled before tests | PASS (static) | 30×2s poll `& $psql ... 'SELECT 1'` sets `$ready`, throws if never ready (L56-62), before the test step |
| Fresh Test Database Every Run | Pre-existing database is replaced | PASS | `DROP DATABASE IF EXISTS pos_test WITH (FORCE)` (L63) + `CREATE DATABASE pos_test` (L65); local equivalent produced 970/970 on an empty DB |
| Coverage Gate and Vulnerability Audit Under PowerShell 7 | Missing coverage report fails actionably | PASS (static) | `shell: pwsh`; `if (-not $report) { ::error::No se generó el reporte...; exit 1 }` (L86-90) |
| Coverage Gate and Vulnerability Audit Under PowerShell 7 | Vulnerable package fails the audit | PASS (static) | `shell: pwsh`; `$audit -match '...vulnerable packages...'` → `exit 1` (L99-104) |
| Frontend Job on Node 24 | npm ci accepts the versioned lockfile | PASS | Local `npm ci` under Node v24.11.1 / npm 11.6.2 → exit 0, 0 vulnerabilities |
| Frontend Job on Node 24 | Declared engine range excludes npm 10 | PASS | `"engines": { "node": ">=24" }` (package.json L6-8); Node 22/npm 10 not satisfied |
| Job Timeout and Smoke Database Isolation | Timeout covers the full Windows run | PASS (static) | `timeout-minutes: 45` (L21); local build 7s + 970-test suite 13s |
| Job Timeout and Smoke Database Isolation | Per-run smoke database suffix | PASS | `SMOKE_DB_SUFFIX: ${{ github.run_id }}` (L75); `pos_smoke_{suffix}` / `pos_zero_{suffix}` naming (WebApplicationFactorySmokeTests L21-33) |

### Spec: test-schema-bootstrap (6 requirements / 9 scenarios)

| Requirement | Scenario | Result | Evidence |
|---|---|---|---|
| Both Schemas Materialized on a Fresh Database | Inventory context bootstraps first | PASS | Check 5: `Products`-only DB → after bootstrap both `Products` and `Users`; bootstrap always materializes Sales then Inventory (TestSchemaBootstrap L39-40) |
| Both Schemas Materialized on a Fresh Database | Sales context bootstraps first | PASS | Check 2: fresh `pos_test` full suite 970/970, no `42P01` |
| Model Parity Preserved | Model columns exist after bootstrap | PASS | `pos_test` `information_schema.columns` for `Users` includes `AccessFailedCount` (and `Username`) |
| Model Parity Preserved | Model seed rows exist after bootstrap | PASS | `pos_test` `Users` row `Id=1, Username=admin` (EF `HasData`) |
| Half-Initialized Database Repair | Inventory-only database gains Sales tables | PASS | Check 5: only `Users` was created; existing `Products` retained |
| Idempotent Bootstrap | Second invocation performs no DDL | PASS | Single-flight `ConcurrentDictionary<string, Lazy<Task>>` per connection key; runs 2 and 3 yielded 970/970 with no duplicate rows |
| Idempotent Bootstrap | Concurrent first callers are serialized | PASS (by construction) | `Lazy<Task>` with `ExecutionAndPublication` + `SemaphoreSlim(1,1)`; no dedicated automated test (see WARNING W1) |
| Failure When a Marker Table Is Missing | Missing marker aborts the run | PASS (code) | `InvalidOperationException` naming the missing marker table and database (L49-63); no dedicated automated test (see WARNING W1) |
| Non-PostgreSQL Providers Unaffected | InMemory context is untouched | PASS | Only Npgsql factory methods (`CreatePostgreSqlSalesDbContext*`) and `HistoryImmutabilityTests` call the bootstrap; InMemory/Sqlite paths do not |

### Spec: test-data-seeding (4 requirements / 8 scenarios)

| Requirement | Scenario | Result | Evidence |
|---|---|---|---|
| Per-Row Idempotent Seeding | Model-seeded payment methods are not duplicated | PASS | `pos_test` after 3 runs: `1 Cash, 2 Card, 3 Punto de Venta, 4 Pago Movil, 5 Zelle`; zero duplicate names. Ids 1-2 from model `HasData` |
| Per-Row Idempotent Seeding | Default customer is created when absent | PASS | `Customers` `Id=1 Consumidor Final IsDefault=true`; exactly 1 default |
| Per-Row Idempotent Seeding | Repeat seeding is a no-op | PASS | Runs 2 and 3: 970/970; no duplicate rows, no unique-index violation |
| Advisory-Lock Serialization on Npgsql | Concurrent seeders create rows exactly once | PASS (by construction) | `SELECT pg_advisory_xact_lock(42000)` inside a single transaction before check/insert (TestDatabaseFactory L110-112); no dedicated automated test (see WARNING W1) |
| Advisory-Lock Serialization on Npgsql | No payment references a missing method | PASS | Seeding commits before sale-payment inserts; `DailyClosureRetry` + sales-history tests green in checks 2-4 |
| No Test Bypasses the Seeder | Sales history test uses the seeder | PASS | Diff: calls `SeedStandardSalesDataAsync(seedContext!)`, resolves `pm` by name, removed ad-hoc `PaymentMethod { Name = "Punto de Venta" }` insert |
| No Test Bypasses the Seeder | Daily closure resolves the method by name | PASS | Diff: `SingleAsync(p => p.Name == "Punto de Venta")`; `PaymentMethodId = puntoDeVentaId` in `SalePayment` and `ClosureDetail` |
| Non-PostgreSQL Seeding Preserved | InMemory seeding unchanged | PASS | `SeedInMemoryAsync` path preserved (TestDatabaseFactory L180-206); full InMemory suite green |

Totals: 16/16 requirements, 28/28 scenarios satisfied; 0 FAIL.

## Findings

### CRITICAL
None.

### WARNING

- **W1 — No automated guard/regression tests for the new bootstrap/seeding behavior.** `design.md` (Testing Strategy, L81-88) promised `RequiresDocker` integration guard tests (both markers, `AccessFailedCount`, `admin` HasData, Ids 1-2 "Cash"/"Card", bootstrap idempotency, two concurrent callers without `23505`, seeder run twice, edge cases (a)-(d)), and `tasks.md` 3.2 references "the guard test". No such test file exists: only `HistoryImmutabilityTests` calls the bootstrap. Consequence: scenarios bootstrap-R4-S7 (concurrent serialization), bootstrap-R5-S8 (missing marker abort), and seeding-R2-S4 (concurrent seeders) rest on code inspection plus the full-suite runtime, not on executable regression coverage. The spec scenarios still hold (checks 2/3/5).
- **W2 — `apply-progress.md` verification evidence is inaccurate.** It records frontend `npm test` as "164/167 pass; 3 OOM failures" (apply-progress L51, tasks.md 6.5), but independent re-run yields **178/178 pass, 0 fail, exit 0**. No functional impact, but the change record understates the current result and should not be relied upon.
- **W3 — Design/implementation deviations (non-breaking).** (a) `design.md` L21 specifies `IRelationalDatabaseCreator.Exists()` → `Create()` for the *database*; the implementation only checks marker tables and relies on the database pre-existing (CI creates it explicitly via `CREATE DATABASE`). (b) `design.md` L37 specifies wrapping the Npgsql seeder in `Database.CreateExecutionStrategy().ExecuteAsync(...)`; the implementation uses a raw Npgsql connection + transaction instead (functionally sound, since it bypasses the EF retrying-strategy transaction restriction). No spec scenario depends on these deviations.

### SUGGESTION

- **S1 — Dead advisory-lock constant.** `TestSchemaBootstrap.SeedLockKey` (`"pos-test-seed-1"`) is declared but never used; the seeder hardcodes `pg_advisory_xact_lock(42000)` (TestDatabaseFactory L111). Consolidate on one shared constant to prevent drift.
- **S2 — Redundant guard.** The `SemaphoreSlim(1,1)` pass guard is redundant given the single-flight `Lazy<Task>`; harmless but removable.
- **S3 — `psql` path assumed.** The CI step derives `$psql` from the newest numeric directory under `C:\Program Files\PostgreSQL` without asserting the file exists; add a `Test-Path` guard for a clearer failure message.
- **S4 — Smoke DB residue.** `pos_smoke_<suffix>` is created per run but never dropped (the per-run suffix avoids collisions). Consider cleanup to avoid residue on long-lived runners.

## Residual Risks

1. **Actual GitHub Actions execution is NOT verifiable locally.** All `windows-2025` runner behavior (PostgreSQL service `Disabled`→`Manual` start, readiness timing, `Set-Service` under `$ErrorActionPreference='Stop'`, artifact upload), the Ubuntu + Node 24 frontend job, and the `timeout-minutes: 45` sufficiency are asserted from static YAML plus local Windows/npm equivalents. The first CI push/PR run is the authoritative confirmation.
2. **Concurrency and marker-failure paths lack automated coverage** (W1); they are verified by construction and by the full-suite runtime only.
3. **Bootstrap assumes the target database exists** (W3a). A wholly absent database would surface an Npgsql error instead of being auto-created by the helper; CI's explicit `CREATE DATABASE` masks this.
4. **Theoretical frontend OOM in CI** remains possible (`apply-progress.md` once recorded 3 OOM failures), though the current local re-run is 178/178 with 0 failures.

## Recommendation

All blocking checks pass with no CRITICAL findings; the change is ready for `sdd-archive`. Address W1 (add the promised guard tests) in a follow-up if executable coverage of the concurrency scenarios is required.
