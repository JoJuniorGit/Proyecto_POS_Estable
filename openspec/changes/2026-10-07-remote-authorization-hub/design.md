# Design: Remote Authorization Hub (Paso 4)

## Context

Paso 4 (`docs/Ideas.txt:153-191`) plus user-expanded criteria (race, local fallback, immutable audit). Paso 3 is partial (SecurityStamp revocation only; no UserSessions/kick/logout push) — non-blocking because push targets role groups and absence of online admins degrades to expiry + local fallback. Clients: Web + WPF (maintainer decision). RDD off (clone-local + global, verified 2026-10-07).

## D1 — Data model (Core entities mapped by SalesDbContext)

- `Core/Entities/AuthorizationRequest.cs`: `Id`, `ActionType` (`AuthorizationActionType`: `ManualPriceOverride=1`, `SaleCancellation=2` — extensible registry), `SaleId (int?)`, `RequestedByUserId (int)`, `RequestedByName (string)`, `Terminal (string?)`, `Status` (`AuthorizationStatus`: `Pending=0/Approved=1/Rejected=2/Expired=3`), `ResolutionMode` (`AuthorizationResolutionMode`: `Remote=1/Local=2`, null while pending/expired), `ResolvedByUserId (int?)`, `ResolvedByName (string?)`, `ResolutionReason (string?)`, `ContextJson (string, ≤4 KB, display payload)`, `ContextHash (varchar(64), SHA-256 hex of the canonical operation payload)`, `CreatedAt`, `ExpiresAt`, `ResolvedAt?`, `ConsumedAt?` (UTC).
- `Core/Entities/AuthorizationAudit.cs` (append-only): `Id`, `RequestId`, `ActionType`, `SaleId?`, `Terminal?`, `RequestedByUserId`, `RequestedByName`, `Status` (terminal), `ResolutionMode?`, `ResolvedByUserId?`, `ResolvedByName?`, `Reason?`, `ContextJson?`, `RequestedAt`, `ResolvedAt`.
- **No FK constraints and no navigations**: names are snapshotted columns, so user/catalog edits never cascade into audit history and user deletion UX is not blocked. Rationale recorded here deliberately.
- `SalesDbContext`: two DbSets + indexes — **partial unique** `(RequestedByUserId, SaleId, ActionType) WHERE Status = Pending` (atomic one-pending dedupe), `(Status, ExpiresAt)` for the sweep, `(RequestId)` on audits. Enum mapping follows the existing `SaleStatus` convention.
- Migration in `Sales.Module/Migrations` (raw-SQL precedent `20261005120000`; `dotnet ef` may be broken in this environment — handcraft with snapshot parity if needed). Guard trigger `BEFORE UPDATE OR DELETE ON "AuthorizationAudits" → RAISE EXCEPTION`, created **only when `migrationBuilder.ActiveProvider` is Npgsql** so SQLite test bootstraps stay green.

## D2 — Domain state machine (`Sales.Module`)

`IAuthorizationService` / `AuthorizationService` (DbContext-backed, no SignalR/JWT dependencies — module boundary preserved):

- `CreateAsync`: requester must be an authenticated non-elevated cashier with access to the sale (ownership rule mirrors `IsAuthorizedForSaleAsync`: own sale, OnHold accessible); `Driver` blocked; action must belong to the registry; `ContextJson` ≤ 4 KB; dedupe returns the existing Pending request with its remaining TTL; persists `ExpiresAt = now + RequestTimeoutSeconds` (default 60).
- `ResolveAsync(requestId, adminUserId/Name, approved, reason)`: atomic `ExecuteUpdateAsync` `WHERE Id = @id AND Status = Pending`; `0` rows → read current state → `AlreadyResolved(resolverName)` (message exact, see spec) / `Expired` / `NotFound`; `1` row → success (mode `Remote`).
- `ResolveLocalAsync(requestId, username, password, reason)`: credential check against Users + `Core.Security.PasswordHasher`; role must be `Admin`/`Manager`; **reuses the login lockout policy from `AuthService`** (failed attempts + lockout window read from the current implementation — no new policy invented); atomic resolve with mode `Local`; returns supervisor identity. The requester's own account is a valid supervisor only if it holds an elevated role — the flow is only reachable by non-elevated requesters, so no self-approval path exists.
- `ExpireStaleAsync`: batch `Pending → Expired WHERE ExpiresAt < now`; returns affected rows for push + audit.
- `TryConsumeAsync(requestId, userId, actionType, saleId, contextHash)`: atomic claim `SET ConsumedAt = now WHERE Id = @id AND Status = Approved AND ConsumedAt IS NULL AND RequestedByUserId = @user AND ResolvedAt + TokenTtl >= now`; binding checks (action/sale/ctx) before the claim; distinct failure reasons (`NotApproved`, `AlreadyConsumed`, `WrongUser`, `WrongAction`, `WrongSale`, `ContextMismatch`, `TokenExpired`).
- Audit: exactly one `AuthorizationAudit` row per terminal transition (Approved/Rejected/Expired) written in the same transaction; **no update/delete surface exists anywhere**.
- `GetAsync`: requester or elevated only.

Elevation rule centralized: `Admin`/`Manager` = elevated; `Driver` blocked from sales; `Cashier` (and any future non-elevated sales role) requests.

## D3 — Ephemeral token (`Backend.API`)

- `AuthorizationTokenService`: mini-JWT HMAC-signed with `JwtSettings.Key`; issuer from settings; audience `pos:authorization`; claims `sub`=cashier id, `jti`=request id, `act`=action, `sal`=sale id, `ctx`=context hash, `exp`=`ResolvedAt + TokenTtlSeconds` (default 60). `Issue()` + `Validate()` (signature, lifetime, audience, issuer). Validation ≠ consumption; consumption is D2 `TryConsumeAsync`.
- Delivery: hub push to `user:{requester}` on approval; `GET /api/authorizations/{id}` re-issues with the SAME expiry window (`ResolvedAt + 60 s`, never extended) for requester recovery after reconnect; local-resolve REST response.
- `AuthorizationContextCanonicalizer`: builds canonical JSON per action from the operation payload (`AddItemRequest`: `ProductId`, `Quantity`, `CustomUnitPriceUsd`, `CustomUnitPriceLocal`; `SaleCancellation`: `SaleId`) with fixed property order and invariant decimals → SHA-256 hex. **The same function runs at create and at consume**; display-only fields (product name) travel in `ContextJson` and are never hashed.

## D4 — Transport (`Backend.API`)

- `AuthorizationHub` at `/hubs/authorization`, `.RequireAuthorization()`. `OnConnectedAsync`: join `user:{sub}` always; join `role:elevated` when `Admin`/`Manager`. Methods: `RequestAuthorization(dto)` → coordinator create (requester from `Context.User`), returns `{requestId, expiresAt}`; `ResolveAuthorization(requestId, approved, reason)` → elevated check → coordinator resolve → outcome incl. exact race message.
- `IAuthorizationNotifier` (`Sales.Module/Interfaces`) implemented by `SignalRAuthorizationNotifier` via `IHubContext<AuthorizationHub>` (pattern: `SignalRHoldOrderNotifier`). Events: `AuthorizationRequested` → `role:elevated` (enriched); `AuthorizationResolved` → `user:{requester}` (token when approved) + `role:elevated` (close modals, include resolver name); `AuthorizationExpired` → both.
- REST pair `AuthorizationsController` (transport fallback for clients whose socket is down — the offline-fallback criterion — plus straightforward integration tests): `POST /api/authorizations` `[Authorize]`; `GET /api/authorizations/{id}` `[Authorize]` (requester/elevated); `POST /{id}/resolve` `[Authorize(Roles="Admin,Manager")]`; `POST /{id}/local-resolve` `[Authorize]` + supervisor credentials body.
- `AuthorizationCoordinator` composes service + token + notifier so hub and controller stay thin and the issue→push→audit orchestration lives in one testable place.

## D5 — Protected-action contract (v1)

- `ManualPriceOverride` — `SalesController.AddItemAsync` (`:127-163`): custom price + non-elevated → read `X-Authorization-Token`; absent/invalid/reused → 403 ProblemDetails (existing message) **plus extensions** `authorizationRequired=true`, `authorizationAction="ManualPriceOverride"`; valid → coordinator consume → `isAuthorized=true` → proceeds through the existing `isPriceOverrideAuthorized` hook. Elevated unchanged. The token gate runs before idempotency resolution for negative outcomes; when the token is already consumed the endpoint falls through to idempotency resolution so a retried successful attempt replays its stored response (HIT) instead of failing, and a MISS in that state returns the 403 contract without executing. Consumption precedes execution: a downstream failure burns the token (fail-closed, documented).
- `SaleCancellation` — **vetoed by the maintainer (tracker L6)**: sale cancellation keeps its current behavior for cashiers; the enum value stays reserved for a future consumer. No gate is implemented in v1.
- Registry is the enum + coordinator switch: Pasos 5/11 add an action type and one consume call — no new approval mechanism.

## D6 — Expiry

- `Authorization:RequestTimeoutSeconds` (default 60) and `Authorization:TokenTtlSeconds` (default 60) in appsettings.
- `AuthorizationExpiryJob` (BackgroundService, 5 s interval; precedent `ReservationExpiryJob`): `ExpireStaleAsync` → audit + push `AuthorizationExpired`.
- Lazy expiry on every resolve/consume path — the job is a notifier, never the only enforcement.
- Client countdown from `expiresAt`; on expiry the wait state shows "Expirada" with retry (new request).

## D7 — Clients

- **Web**: `src/services/authorizationHub.js` (second connection, cookie auth, auto-reconnect, invoke + REST fallback); `AuthorizationWaitModal` (blocking, exact "Esperando autorización remota...", countdown, "Autorización Local" + supervisor form, "Cancelar") as infrastructure for future protected actions (the net-new "Precio manual" web UI is vetoed — tracker L6 — so there is no Web-side trigger in v1); `AuthorizationNotifications` mounted for Admin/Manager (approve/reject + optional reason); `salesApi.addItemToSale` optional `X-Authorization-Token`; 403 interception via ProblemDetails extensions (`api.js` must propagate them).
- **WPF**: `AuthorizationHubService` (`HubConnection` with `access_token` per `ExchangeRateService` pattern, `IDisposable`, events/`WeakReferenceMessenger`); wait dialog + local dialog + admin notification dialog; integration at `PosViewModel.Orders.cs:131` (custom-price catch → wait flow → retry with token); dispatcher marshalling and teardown dispose per the WPF discipline skill.
- Reconnect robustness: `GET /api/authorizations/{id}` recovery on hub reconnect; push loss degrades to countdown expiry.
- Notification template (faithful to the user's example "Cajero 01 solicita aplicar 20% de descuento a Factura #445"): "El cajero {cashier} solicita autorización para {acción} en la Factura #{saleId}. {detalle}".

## D8 — Test strategy

- Backend: unit (state machine incl. double-resolve race, token issue/validate, canonicalizer) + integration (controllers; hub groups/push via TestServer — precedent `PaymentMethodSignalRIntegrationTests`); Postgres-gated tests for the trigger and true concurrency; atomic claims asserted with sequential re-resolution otherwise.
- Web: `node --test` for the wait state machine, payload builder and token header (components stay structural — runner has no DOM; 8.149 precedent).
- WPF: ViewModel tests (wait/countdown, local flow, notifications, dispose).
- Gates: build 0/0; full suite green; coverage Core ≥ 0.70 / Sales ≥ 0.80 / Inventory ≥ 0.72; `npm test` + lint; gated E2E if the environment allows (flagged).
- RED→GREEN per task with recorded evidence (ODD); independent verifier per repo practice.

## D9 — Delivery

11 work units (tasks.md); chain **stacked-to-main** (cached); work-unit commits `feat(8.150)`; ANEXO 8.150; RDD off (clone-local + global, verified 2026-10-07); push/PR/merge = maintainer decision.
