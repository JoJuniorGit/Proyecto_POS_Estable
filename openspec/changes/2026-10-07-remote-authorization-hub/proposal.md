# Proposal: Remote Authorization Hub (Paso 4)

## Intent

Deploy a real-time remote authorization system for critical cash operations (Paso 4, `docs/Ideas.txt:153-191`) with the user-expanded criteria: dynamic privilege escalation, ephemeral 60-second tokens, race-safe first-response resolution, 60-second timeout with local supervisor fallback, and an immutable audit trail. The authorization layer is built generic (action registry + hub + token + audit) so future modules — stock shrinks (Paso 5) and credits (Paso 11) — consume it natively instead of growing ad-hoc approval patches.

## Scope

- **In scope**: `AuthorizationHub` + REST pair, authorization request/state machine, ephemeral mini-JWT (60 s, single-use, payload-hash-bound), race-safe resolution, expiry (lazy + sweep), local fallback with supervisor credentials, immutable audit (append-only + Postgres guard trigger), protected-action wiring for `ManualPriceOverride` (Web + WPF) and `SaleCancellation` (Web; backend gate), Web + WPF wait/notify/local flows.
- **Out of scope**: completing Paso 3 (UserSessions/kick/active-users — documented as partial, non-blocking); percentage-discount and credit domains (Pasos 9/11 will consume the hub); PIN management; WPF cancel UI (does not exist today).
- **Affected projects and project-local commands**:
  - Backend: `Core`, `Sales.Module`, `Backend.API` → `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`
  - Web: `Web.Frontend` → `npm test` + `npm run lint`
  - Desktop: `Desktop.Client.Core`, `Desktop.Client` → same .NET suite
  - Build gate: `dotnet build CommandCenter.slnx -c Release` (0/0)

## Approach (one line per layer)

- **Data**: `Core/Entities/AuthorizationRequest` + `AuthorizationAudit` (+ enums) mapped by `SalesDbContext`; migration with partial unique pending index, expiry index, and a Postgres-only UPDATE/DELETE guard trigger on audits.
- **Domain**: `Sales.Module` state machine — create (ownership + dedupe), atomic resolve (first wins), local credential resolution (lockout reused), lazy/batch expiry, atomic single-use token claim, append-only audit on every terminal transition.
- **Token**: mini-JWT signed with `JwtSettings` key, audience `pos:authorization`, 60 s from resolution, claims bind requester/action/sale/canonical context hash.
- **Transport**: `AuthorizationHub` (`/hubs/authorization`) + `AuthorizationsController` REST pair; groups `user:{id}` and `role:elevated`; notifier interface in `Sales.Module` implemented via `IHubContext` (pattern `SignalRHoldOrderNotifier`).
- **Protected actions**: AddItem consumes the token through the existing `isPriceOverrideAuthorized` hook; cancel gains the same gate for non-elevated users; 403 ProblemDetails extensions (`authorizationRequired`, `authorizationAction`) let clients open the wait flow.
- **Clients**: Web + WPF wait state (blocking, countdown, "Autorización Local"), admin notifications with approve/reject, race message, expiry/rejection outcomes, token retry.

## Risks

- Hub auth in both clients (cookie for Web, `access_token` query for WPF) must be verified end-to-end; a silent auth failure would strand wait states — covered by hub integration tests + status recovery.
- The raw PL/pgSQL guard trigger must be provider-guarded (`migrationBuilder.ActiveProvider`) so SQLite-based test bootstraps keep working.
- Cancellation gating is a behavior change for cashiers (flagged in exploration.md); reversible in one task if vetoed.
- Token consumption happens before the protected operation executes: a downstream failure burns the token (fail-closed, documented; cashier re-requests).
- Concurrent double-resolution is enforced by an atomic conditional UPDATE, not optimistic retries — verified with sequential double-resolve plus optional gated Postgres concurrency test.
- Two new SignalR connections per client (existing exchange-rate + authorization) — lifecycle/dispose covered by WPF tests and web reconnect handling.

## Rollback

Revert the work units (stacked chain); migration Down drops the trigger, index and both tables. No data rewrites anywhere. Client changes are additive (new components/services); server gates revert to the pre-change 403/role behavior.

## Delivery

`ask-on-risk`; chain **stacked-to-main** (cached V0.15 chain policy); 11 chained work units; work-unit commits `feat(8.150)`; ANEXO 8.150; RDD off (clone-local + global, verified 2026-10-07); push/PR/merge remain maintainer decisions.
