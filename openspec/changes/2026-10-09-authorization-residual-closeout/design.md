# Design: Authorization Residual Closeout (8.151)

## D1 — NULL-safe pending dedupe (D1/R3)

- The pending partial unique index (`20261007120000_AddRemoteAuthorizationHubDataLayer`, raw SQL) is NOT yet shipped → edit in place: on Npgsql create it as `NULLS NOT DISTINCT` (PostgreSQL 15+; repo targets 16/18). Go-forward-friendlier: a new migration for environments where the old migration was already applied? The branch has not been pushed (CI never applied it), but local dev databases MAY have applied `20261007120000`. **Decision: new additive migration** `2026-10-10-*_PendingDedupeNullSafe` that drops and recreates the index with `NULLS NOT DISTINCT` (Npgsql-guarded), leaving the original migration immutable — correct for every environment. Down restores the previous index.
- Model (`SalesDbContext`) stays unchanged (portable filter `"Status" = 0`); SQLite keeps NULL-distinct semantics — Postgres-gated test pins the closed gap (duplicate `(user, NULL, action)` Pending rejected with 23505).
- Service collision path already translates that unique violation (constraint-name/tuple detection) → works for NULL once the index enforces it.

## D2 — Atomic consume binding (R1)

- `TryConsumeAsync` claim UPDATE adds the binding conditions to the WHERE (`ActionType`, `SaleId`, `ContextHash` alongside status/consumed/user/TTL). On 0 affected rows, a classification read distinguishes the failure reasons (preserving the precise `WrongAction/WrongSale/ContextMismatch/AlreadyConsumed/...` outcomes). No pre-read remains on the success path → no TOCTOU.

## D3 — Lazy-expiry loser classification (R2)

- In `ResolveAsync`/`ResolveLocalAsync` lazy-expiry branch, when the expire-claim affects 0 rows, re-read the row before reporting `Expired` (a concurrent approval/rejection wins the classification).

## D4 — Requester cancellation (D4b)

- `AuthorizationStatus` gains `Cancelled = 4` (int column; no migration). Terminal, audited like the others (`AuthorizationAudit.Status = Cancelled`, `ResolutionMode = null`, `ResolvedAt` set).
- `AuthorizationService.CancelAsync(requestId, requesterUserId)`: atomic `Pending → Cancelled WHERE RequestedByUserId = requester`; outcomes `Cancelled` / `AlreadyResolved(name)` / `Expired` / `NotFound` / `Forbidden` (not the requester). Coordinator pushes `AuthorizationResolved` (status Cancelled, no token) to requester + elevated so modals close; admin UIs render "Solicitud cancelada por el cajero." instead of the race message for this status.
- REST `POST /api/authorizations/{id}/cancel` `[Authorize]` (requester-only; others 403/409 per outcome). Clients: web `cancelWait` and WPF `cancelWait` fire-and-forget the cancel (best-effort; failure does not block the local close). E1 gains a cancellation scenario (cancel → status Cancelled + audit + elevated closure; cannot cancel after approval).

## D5 — Web contract outcome (D3/W3)

- `authorizationFlow.js` reducer gains an explicit settled outcome on rejection/expiry (`{ ok:false, outcome:'rejected'|'expired', reason }`); the provider resolves the caller promise at that transition while keeping the acknowledgment modal open (single-wait guard released only when the dialog closes). Reducer tests pin the semantics; provider wiring remains thin (no-DOM runner limitation documented).

## D6 — WPF reconnect reconciliation + polish (D4a/D4c/D4d)

- `AuthorizationHubService` exposes a `Reconnected` event (wired to SignalR `Reconnected`); the notification VM re-syncs each queued request via `GetStatusAsync` and applies resolved/expired/cancelled. Countdown display floors (`Math.Floor` on remaining). Own-resolved set entries pruned (timestamp or removal on matching push). Notification dialog captures initial focus on open.
- Web own-resolved ref pruning mirrors the WPF behavior.

## D7 — WPF manual-price trigger (D6/G1)

- Product-suggestion flow gains a "Precio manual" action (button + hotkey-visible affordance per the POS patterns): opens a small USD/Bs input dialog (validated, Bs auto-derived from the current rate when blank), then calls the existing `AddItemAsync` with `customUnitPriceUsd/Local`. Cashiers → backend 403 contract → T10 wait flow (unchanged); elevated → direct. Existing cash-advance branch untouched.
- Tests: trigger VM/dialog tests (validation, cancellation, mapping) + PosViewModel wiring test (custom price reaches AddItemAsync; gate path still routed).

## D8 — WebSockets + PUT pin + spec qualifications (D2/D5)

- WPF-E2E gated test (`FullStackHubWebSocketTests`): connects a SignalR client to the real backend from `FullStackFixture` WITHOUT forcing transport; asserts the negotiated transport is WebSockets and a create/resolve roundtrip works. Runs in the Windows CI job where the real Kestrel backend is already up.
- PUT pin test: non-elevated + custom price on `PUT /{id}/items` keeps the repository's 403 (no `authorizationRequired` extensions, no token flow) — deliberate scope pin.
- Spec text: qualify the `AlreadyConsumed` + missing `Idempotency-Key` case (mandatory-key 400 precedes the 403 contract).

## D9 — Delivery

Work units W1-W5 + closure; commits `fix(8.151)`; ANEXO 8.151; RDD off (clone-local + global); E2 stays cancelled (stash preserved).
