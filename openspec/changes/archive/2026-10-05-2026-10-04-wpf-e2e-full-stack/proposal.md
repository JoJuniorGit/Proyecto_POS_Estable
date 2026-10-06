# Proposal: WPF E2E Full-Stack (Level B) & UIA Stability

## Intent

The desktop E2E suite verifies UI rendering and navigation against an in-app HTTP mock: sales, cash drawer and
daily closure tests never execute the real function. Add a full-stack harness (real `Backend.API` + real
PostgreSQL + the WPF client without `--e2e`) with real business flows (complete POS sale; cash drawer and
daily closure), bounded UI-automation retries so transient COM faults stop producing false reds, and a CI job
that runs the suite.

## Scope

### In Scope
- Full-stack fixture: free-port selection, backend process with an isolated `pos_e2e_<suffix>` database,
  `/api/health` readiness wait, API bootstrap (forced admin password rotation, exchange rate, test product),
  safe client-settings neutralization (backup/restore of `%LocalAppData%\ProyectoPOS\client_settings.json`),
  client launch without `--e2e` via `BackendSettings__BaseAddress`, full teardown (processes, DB drop,
  settings restore).
- Real flows driven through the UI only: complete POS sale (login → search → cart → checkout → payment →
  invoice visible → sale verifiable in Sales History); cash drawer open/operate/close; daily closure execution
  with coherent state/totals.
- UIA stability: bounded retry helper for transient UI-automation faults, configurable timeouts, applied to
  known flaky spots; retries never mask assertion failures.
- CI: `wpf-e2e` job on windows-2025 with PostgreSQL (build, E2E DB, env, suite, artifacts on failure).
- Gating: local runs without the E2E Postgres env return silently (repo pattern); under CI absence fails closed.

### Out of Scope
- Removing or changing the existing `--e2e` mock mode (kept for fast health/navigation coverage).
- Web/Playwright E2E changes; new product features; UI refactors beyond test hooks needed for automation.

## Capabilities

### New Capabilities
- `wpf-e2e-full-stack`: full-stack harness lifecycle + real sale/cash-drawer/closure flows.
- `wpf-e2e-stability`: bounded UIA retry policy and configurable timeouts.

### Modified Capabilities
- `ci-pipeline`: new desktop E2E job.

## Approach

Extend the E2E project: a new `FullStackFixture` (composition of process + DB + bootstrap helpers) reusing the
existing `WpfAppFixture` launch mechanics; an `E2eApiClient` for fixture-side HTTP setup against the real
backend (login, change-password, exchange-rate, product creation); a `UiaRetry` helper wrapping FlaUI finds;
new flow test classes (sale, cash drawer, closure) gated by env; CI job modeled after the backend job.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `tests/CommandCenter.Wpf.E2ETests/Fixtures/*` | New/Modify | Full-stack fixture, API bootstrap client, UIA retry helper |
| `tests/CommandCenter.Wpf.E2ETests/Tests/*` | New/Modify | Sale, cash drawer, closure flows; flakes hardened |
| `tests/CommandCenter.Wpf.E2ETests/CommandCenter.Wpf.E2ETests.csproj` | Modify | Any packaging needed for backend process launch |
| `Desktop.Client/Program.cs` / `App.xaml.cs` | Modify (minimal) | Only if a test-only env/config hook is required for the real-URL launch |
| `.github/workflows/ci.yml` | Modify | `wpf-e2e` job |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| UIA flakes under CI (COM timeouts) | High | Bounded retries + configurable timeouts; job initially mirrors local hardening; artifacts on failure |
| Real-stack setup drift (ports, forced password rotation, DB lifecycle) | Med | Deterministic bootstrap via real API; isolated DB per run; health gating before UI |
| Client machine state (persisted server address) breaking the harness | Med | Fixture backs up/restores the settings file and forces `BackendSettings__BaseAddress` |
| Flows depend on business preconditions (open drawer, rate) | Med | Fixture sets rate and creates product via API; closure flow follows the real state machine discovered during implementation |
| CI runtime cost (backend process + UI automation) | Med | Single job, `timeout-minutes` bounded; suite kept small and targeted |

## Rollback Plan

- New tests/fixtures are additive; removing the CI job and the fixture files disables Level B entirely.
- The `--e2e` mock path remains untouched; existing 18 tests keep running without any env.

## Dependencies

- PostgreSQL reachable locally/CI (same instance used by backend tests), windows-2025 CI runner, FlaUI (present).

## Success Criteria

- [ ] Full-stack fixture boots backend+DB, bootstraps state, launches the real client, and tears everything down cleanly.
- [ ] Complete sale flow passes against the real stack and the sale is verifiable in Sales History.
- [ ] Cash drawer and daily closure flows execute for real with coherent totals.
- [ ] UIA retries are bounded and never mask assertion failures; known flaky checks pass repeatedly (≥3 consecutive local runs).
- [ ] `wpf-e2e` CI job runs the suite with PostgreSQL and uploads artifacts on failure.
