# Exploration: Remote Authorization Hub (Paso 4)

## Context

Roadmap `docs/Ideas.txt` Paso 4 (L153-191); user-expanded criteria (race-safe first response, local fallback, strict immutable audit). Declared dependency: Paso 3 session control (L105-149). Branch `V0.15` at `76f69c8`; RDD off (clone-local + global, verified 2026-10-07).

## Question

What exists today to build on, and what is genuinely missing?

## Findings (read-only, path:line evidence)

- **Paso 3 is PARTIAL**: SecurityStamp-based token revocation exists (`Core/Entities/User.cs:23-27`, `Backend.API/Services/SecurityStampValidator.cs:34-115`, `Backend.API/Middleware/SecurityStampValidationMiddleware.cs:27-71`), but there is NO `UserSessions` table / refresh tokens (0 repo matches), no active-users view, no kick endpoint, and no SignalR logout push. Login does not invalidate prior sessions (`AuthController.cs:33-86`; `TokenService.cs:63-66`). Not blocking for this feature: push targets role groups; no online admin degrades to expiry + local fallback.
- **Single existing hub**: `ExchangeRateHub` (`Backend.API/Hubs/ExchangeRateHub.cs:6`) mapped at `/hubs/exchange-rate` (`PipelineExtensions.cs:89`); auth via HttpOnly cookie `pos_jwt` (`AuthController.cs:64-79`) or `access_token` query for `/hubs` paths. Web client `Web.Frontend/src/services/signalr.js:42-65`; WPF connection `Desktop.Client.Core/Services/ExchangeRateService.cs:28,254-304`.
- **Roles**: exactly `Cashier, Manager, Admin, Driver` (`Core/Entities/User.cs:3-9`). Password hashing `Core/Security/PasswordHasher.cs`; login lockout handled in `Sales.Module/Services/AuthService.cs`.
- **Protected-action hook already exists**: `Backend.API/Controllers/SalesController.cs:127-163` — custom unit price + non-elevated → immediate 403 (`:135-139`); passes `isAuthorized` as `isPriceOverrideAuthorized` into `ISalesService.AddItemAsync` (`Sales.Module/Interfaces/ISalesService.cs:15`). WPF reachable path: `PosViewModel.Orders.cs:131`. Web has NO manual-price UI (`Web.Frontend/src/services/salesApi.js:31` only sends quantity/rate).
- **Cancellation**: endpoint `SalesController.HoldOrders.cs:144-158` (Admin/Manager/Cashier + per-sale auth); service `Sales.Module/Services/SalesService.cs:406-427`; web UI `PendingOrdersPage.jsx:156-167`; WPF has no cancel path.
- **No discounts/credit-ledger domain exists**; no OTP/ephemeral-token helper (0 matches).
- **Persistence**: `SalesDbContext` (`Sales.Module/Data/SalesDbContext.cs:9`) holds Users/Sales/Outbox/IdempotentRequests; entities live in Core and are mapped by module contexts (User pattern); per-module migrations; raw-SQL migration precedent `20261005120000`; `dotnet ef` was broken in this environment (ANEXO 8.149 D3).
- **Tests**: `CommandCenter.Tests` (xUnit + WebApplicationFactory; SignalR test precedent `PaymentMethodSignalRIntegrationTests.cs`); web `node --test` + oxlint; WPF FlaUI E2E separate. `TEST_POSTGRES_CONNECTION` optional (Postgres-specific tests skip/fail without it).
- **SDD**: no active change; dir naming `YYYY-MM-DD-<slug>`; `openspec/config.yaml` strict_tdd false, coverage gates Core .70 / Sales .80 / Inventory .72; build gate `dotnet build CommandCenter.slnx -c Release`; latest ANEXO 8.149 → this feature uses ANEXO 8.150.

## Decisions derived (see design.md)

- Reuse the existing `isPriceOverrideAuthorized` hook instead of redesigning service APIs.
- Store requests/audits as Core entities mapped by `SalesDbContext`; no new module.
- Mini-JWT ephemeral token (60 s) with single-use atomic consume + canonical payload-hash binding.
- Race safety via atomic `ExecuteUpdateAsync` `WHERE Status = Pending` (first response wins).
- Local fallback validates real supervisor credentials (no PIN concept exists) with login lockout policy.
- Expiry: 60 s lazy + background sweep, audited and pushed.
- Clients: Web + WPF (user decision "3"), both as requesters and as admin resolvers.

## Flagged assumptions (maintainer may veto before the affected tasks)

- `SaleCancellation` gating for non-elevated users is a deliberate behavior change (roadmap lists "anulaciones" as critical).
- Web "Precio manual" action is net-new minimal parity so the web terminal can exercise the protected path.
- Local fallback uses real credentials (there is no PIN management in the system).
