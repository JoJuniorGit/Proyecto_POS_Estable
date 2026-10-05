# Exploration: WPF E2E Full-Stack (Level B) & UIA Stability

Date: 2026-10-04 · Orchestrator read-only mapping (file reads + greps); no code changes.

## Current state (evidence)

- `WpfAppFixture.Launch()` starts the real `Desktop.Client.exe` with `--e2e`
  (`tests/CommandCenter.Wpf.E2ETests/Fixtures/WpfAppFixture.cs:53-57`).
- `--e2e` swaps **all** HTTP for `E2EMockHttpMessageHandler`
  (`Desktop.Client/App.xaml.cs:137-147, 174`; mock at `Desktop.Client/Services/E2EMockHttpMessageHandler.cs`, 909 lines).
  No real backend, no PostgreSQL, no SignalR.
- Test inventory (18): only `PendingPickupsTests` (4) executes a business flow; `LoginViewTests` (2, real VM),
  `E2EAppHealthTests` (2, login/rate/no-error-dialogs/navigation of 12 views); the rest (~10) are navigation
  smokes (`PosSaleTests`, `DailyClosureTests`, `CashDrawerTests`, `SalesHistoryTests`, `InventoryTests`,
  `PendingOrdersTests`, `WpfIndependentFlowsTests`).
- Client server resolution: `clientSettings.ServerBaseAddress` (persisted at
  `%LocalAppData%\ProyectoPOS\client_settings.json`; `ClientSettingsStore.cs:36-40`) wins unless blank or the
  default `http://localhost:5000/`, then `Configuration["BackendSettings:BaseAddress"]` applies
  (`App.xaml.cs:157-161`).
- Backend boot: `DatabaseInitializer` probes PostgreSQL, runs `MigrateAsync` on both contexts and seeds from
  config (`Backend.API/Startup/DatabaseInitializer.cs:109-110, 347-401`): admin user (Cedula=Username=seed
  username, `MustChangePassword=true`), default customer, cash-advance product, payment methods
  (`PaymentMethodDefaults.CreateDefault`, `:523-533`). No exchange-rate or catalog product seeds.
- Health: anonymous `GET /health` and `GET /api/health` (`Backend.API/Controllers/HealthController.cs:37-40`),
  exempt from the version-check middleware; returns 503 until the DB is reachable.
- Login: `Backend.API/Controllers/AuthController.cs:34`. `ASPNETCORE_URLS`/launchSettings default
  `http://localhost:5000`; Development profile loads `appsettings.Development.json` (dev connection string +
  dev seed password) with env vars overriding.
- CI (`ci.yml`): backend job (windows-2025, starts preinstalled PostgreSQL, creates `pos_test`) and frontend
  job; **E2E (WPF or Playwright) does not run in CI**.
- Known flakes: full E2E runs hit `COMException 0x80131505 Operation timed out` (FlaUI `FindFirst`) in
  `PendingPickupsTests`; isolated re-runs pass 4/4. No retry policy exists.

## Implications

- Level B requires a process harness: backend process + isolated DB + health wait + API bootstrap (rotate the
  forced admin password, set the rate, create a test product) + safe client-settings neutralization + client
  launch without `--e2e` via `BackendSettings__BaseAddress` on a fixture-chosen free port.
- Gating mirrors the repo's Postgres pattern: silent return locally without env; fail closed under CI.
- The admin `MustChangePassword=true` makes the API bootstrap mandatory before any UI login.
- UIA stability needs a bounded retry helper around finds/waits (retry on COMException only), applied to the
  known flaky dialog checks; timeouts should be env-configurable.
- CI can host the full-stack job on windows-2025 (PostgreSQL preinstalled; same pattern as the backend job).
