# Proposal: Authorization Residual Closeout (8.150 residuals → 8.151)

## Intent

Close every residual registered for feature 8.150 (remote authorization hub): `docs/reporte.txt` ANEXO 8.150 §D, `odd/tasks/autorizaciones-remotas-signalr.md` (L5-L16) and the final verification report. Fix what is fixable; for scope-by-design items, pin the behavior with tests/docs so it is deliberate instead of accidental.

## Scope

- **In scope**:
  - **D1/R3**: the partial unique pending index does not cover `SaleId IS NULL` → on PostgreSQL the (unshipped) migration index becomes `NULLS NOT DISTINCT`; the service dedupe/collision path covers NULL.
  - **R1**: `TryConsumeAsync` binding (action/sale/context) becomes part of the atomic claim condition (no pre-read TOCTOU).
  - **R2**: lazy-expiry loser path re-reads before classifying (no false `Expired` over a concurrent win).
  - **D4b**: requester **cancellation** of a Pending request — new terminal `Cancelled` status (enum int, no migration), `POST /api/authorizations/{id}/cancel` (requester-only), audit row, elevated closure push; web/WPF cancel wiring; admin UI treats it as closure ("Solicitud cancelada por el cajero.").
  - **D3/W3**: web wait state resolves the caller promise with a distinguishable outcome on rejection/expiry (pure reducer + provider), preserving the acknowledgment modal.
  - **D4a**: WPF notification queue reconciles on hub reconnect via `GetStatusAsync` (Web parity).
  - **D4c**: countdown display floors instead of ceilings (web + WPF).
  - **D4d**: own-resolved id sets pruned (web + WPF).
  - **D2**: a gated WPF-E2E test proving a REAL WebSockets hub transport against the real Kestrel backend (TestServer LocalRAG is LongPolling-only by design).
  - **D5**: pin with a test that `PUT /{id}/items` keeps its role-based gate (no token flow) — deliberate scope.
  - **D6/G1**: WPF "Precio manual" trigger for NORMAL products (dialog + POS wiring) so cashiers can actually exercise the protected flow end-to-end.
  - **Spec qualifications**: the `AlreadyConsumed` + missing `Idempotency-Key` case returns the repository's mandatory-key 400 first (spec sentence qualified).
- **Out of scope**: E2 (WPF admin-notification UI E2E) — cancelled by the maintainer; WIP preserved in `stash@{0}`. Web manual-price UI and sale-cancellation gating remain vetoed (L6).
- **Affected projects**: `Core`/`Sales.Module`/`Backend.API` (`dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`), `Web.Frontend` (`npm test`, `npm run lint`, `npm run build`), `Desktop.Client(-Core)`, `tests/CommandCenter.Wpf.E2ETests` (gated), plus e2e file `AuthorizationEndToEndPostgresTests.cs` (new cancel scenario).

## Approach

- **W1 backend**: migration raw-SQL index → `NULLS NOT DISTINCT` (Npgsql branch; SQLite model unchanged) + service collision path; atomic claim WHERE includes action/sale/hash with post-`0-row` classification read; lazy-expiry loser re-read; cancellation (service + coordinator + controller + notifier + audit + E2E scenario).
- **W2 web**: reducer outcome on rejection/expiry (tests), cancel call, `Cancelled` closure handling in admin notifications, floor countdown, own-resolved pruning.
- **W3 WPF**: hub-service reconnect event (if missing) + notification reconciliation, cancel call, `Cancelled` handling, floor countdown, pruning, initial focus on the notification dialog.
- **W4 WPF trigger**: product-suggestion "Precio manual" action → USD/Bs input dialog → `AddItemAsync` with custom prices (cashier → gate/wait flow; elevated → direct).
- **W5 extras**: WebSockets gated test in the WPF E2E project; PUT pin test; docs.

## Risks

- Cancellation adds a terminal status consumed by both clients and the notifier — covered by service/controller/client tests + an E1 E2E scenario.
- `NULLS NOT DISTINCT` requires PostgreSQL 15+ (repo targets 16/18) and must not alter SQLite behavior (model untouched; Postgres-gated test pins it).
- WPF manual-price trigger adds UI surface; mitigated by VM/unit tests and the existing gate/flow tests.
- WebSocket gated test depends on the local WPF-E2E harness (currently env-blocked for UI scenarios; a hub-only test does not require UIA).

## Rollback

Revert work units in order; migration change is raw-SQL index recreation (Down restores the previous index definition). No data rewrites. Client changes are additive/incremental.

## Delivery

Work units W1-W5 (+ closure), branch V0.15, commits `fix(8.151)`, ANEXO 8.151, gated E2E evidence; push/PR/merge = maintainer decision.
