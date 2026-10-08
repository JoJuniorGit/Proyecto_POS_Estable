# Tasks: Remote Authorization Hub (Paso 4)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~2,600–3,600 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 11 chained work units |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached from the V0.15 chain) |

Decision needed before apply: No
Chained PRs recommended: Yes
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Data layer + audit guard (T1) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~AuthorizationPersistence"` | Migration Down drops trigger/indexes/tables |
| 2. Domain state machine (T2) | `--filter "FullyQualifiedName~AuthorizationService"` | Revert service; no schema |
| 3. Token + coordinator + notifier (T3) | `--filter "FullyQualifiedName~AuthorizationToken\|~AuthorizationCoordinator"` | Revert services; no schema |
| 4. Hub + REST (T4) | `--filter "FullyQualifiedName~AuthorizationHub\|~AuthorizationsController"` | Unmap hub/endpoints; revert DI |
| 5. Protected action — AddItem token path (T5) | `--filter "FullyQualifiedName~ProtectedAction\|~PriceOverride"` | Revert gate to current 403/roles |
| 6. Expiry job (T6) | `--filter "FullyQualifiedName~AuthorizationExpiry"` | Unregister job |
| 7. Web core — hub client + wait flow (T7) | `npm test` (Web.Frontend) | Revert web services/components |
| 8. Web — admin notifications + parity (T8) | `npm test` (Web.Frontend) | Revert web components |
| 9. WPF core — hub service + dialogs (T9) | `--filter "FullyQualifiedName~AuthorizationWait"` | Revert WPF services/dialogs |
| 10. WPF — notifications + integration (T10) | `--filter "FullyQualifiedName~AuthorizationNotification"` | Revert WPF wiring |
| 11. Closure (T11) | full suite + coverage + build 0/0 | — |

## Phase 1: Data layer (T1, S6)

- [ ] 1.1 `Core/Entities/AuthorizationRequest.cs`, `AuthorizationAudit.cs`, enums (`AuthorizationActionType`, `AuthorizationStatus`, `AuthorizationResolutionMode`) — RED: persistence/model tests. (remote-authorization: audit requirement)
- [ ] 1.2 `SalesDbContext` DbSets + indexes: partial unique Pending `(RequestedByUserId, SaleId, ActionType)`, sweep `(Status, ExpiresAt)`, audit `(RequestId)`; enum mapping follows `SaleStatus` convention.
- [ ] 1.3 Migration + Npgsql-guarded append-only trigger; snapshot parity (`dotnet ef` may be broken — handcraft per raw-SQL precedent `20261005120000`); RED: duplicate pending rejected, trigger rejects UPDATE/DELETE (Postgres-gated, skips without `TEST_POSTGRES_CONNECTION`).

## Phase 2: Domain state machine (T2, S4/S5/S6/S12)

- [ ] 2.1 `CreateAsync`: ownership (mirror `IsAuthorizedForSaleAsync`), Driver blocked, elevated rejected with exact message, context ≤ 4 KB, dedupe returns existing pending with remaining TTL — RED tests.
- [ ] 2.2 `ResolveAsync`: atomic conditional UPDATE, first wins, `AlreadyResolved`/`Expired`/`NotFound` outcomes, exact race message; lazy expiry — RED tests (sequential double-resolve; optional Postgres-gated concurrency).
- [ ] 2.3 `ResolveLocalAsync`: credential verification + elevated-role check + login lockout reuse + mode Local — RED tests (wrong password, locked account, success).
- [ ] 2.4 `ExpireStaleAsync` + `GetAsync` (requester/elevated) + append-only audit on every terminal transition — RED tests.
- [ ] 2.5 `TryConsumeAsync`: atomic single-use claim, binding failures (user/action/sale/context), token window bound to `resolvedAt + 60 s` — RED tests.

## Phase 3: Token + coordinator (T3, S3/S11)

- [ ] 3.1 `AuthorizationTokenService` (issue/validate; audience `pos:authorization`; claims sub/jti/act/sal/ctx; exp = resolvedAt + 60 s) — RED tests.
- [ ] 3.2 `AuthorizationContextCanonicalizer` (determinism; identical at create and consume; display fields excluded) — RED tests.
- [ ] 3.3 `AuthorizationCoordinator` (create/resolve/local/consume + token issue + push orchestration) — RED tests with mocked notifier.
- [ ] 3.4 `IAuthorizationNotifier` (interfaz). La implementación `SignalRAuthorizationNotifier` (grupos `user:{id}`, `role:elevated`; eventos `AuthorizationRequested/Resolved/Expired`) viaja a T4 junto con el hub.

## Phase 4: Transport (T4, S1/S2/S4)

- [ ] 4.1 `AuthorizationHub` (`/hubs/authorization`, `RequireAuthorization`, group join on connect, elevated check on resolve) + mapping + DI — RED: integration test (TestServer, precedent `PaymentMethodSignalRIntegrationTests`).
- [ ] 4.2 `AuthorizationsController`: `POST /api/authorizations`, `GET /{id}` (requester/elevated; token recovery never extends window), `POST /{id}/resolve`, `POST /{id}/local-resolve` — RED: integration tests.
- [ ] 4.3 Hub push integration: `AuthorizationRequested` to elevated group; `AuthorizationResolved` to requester group; REST fallback parity.

## Phase 5: Protected action — AddItem token path (T5, S3/S10)

- [x] 5.1 AddItem token path (`X-Authorization-Token` → consume → existing `isPriceOverrideAuthorized` hook) + 403 ProblemDetails extensions before idempotency — RED tests: valid token adds; missing/reused/mismatched → 403 no mutation; replay after success unaffected. (Corrección post-verificación: binding de venta/usuario no circular, L10.)
- ~~5.2 Cancel gate for non-elevated~~ — VETADO por el mantenedor (tracker L6): la anulación conserva el comportamiento actual para cajeros; el valor de enum queda reservado para un futuro consumidor.

## Phase 6: Expiry job (T6, S5)

- [x] 6.1 `AuthorizationExpiryJob` (barrido configurable, default 5 s, primer tick inmediato; precedente `ReservationExpiryJob`) + registro hosted + push vía coordinator — RED tests deterministas (TCS); verificación independiente PASS.

## Phase 7: Web client (T7/T8, S1/S2/S5/S8)

- [ ] 7.1 `src/services/authorizationHub.js`: connection `/hubs/authorization` (cookie), auto-reconnect, invoke + REST fallback, status recovery — RED: `node --test`.
- [ ] 7.2 Wait flow module + `AuthorizationWaitModal` (exact "Esperando autorización remota...", countdown, "Autorización Local", cancel) + supervisor credential form + retry with `X-Authorization-Token` + rejection/expiry outcomes — infrastructure for future protected actions (no Web-side trigger in v1; validated with simulated refusals) — RED tests (structural where the runner has no DOM).
- [ ] 7.3 `AuthorizationNotifications` for Admin/Manager: approve/reject + reason, race message exact, close-on-resolved — RED tests.
- [ ] 7.4 403 interception (ProblemDetails extensions propagation in `api.js`/sales flows) + `salesApi.addItemToSale` optional token header — RED tests. ("Precio manual" web y anulación: VETADOS, L6.)

## Phase 8: WPF client (T9/T10, S1/S2/S5)

- [ ] 8.1 `AuthorizationHubService` (access_token query per `ExchangeRateService`; `IDisposable`; events/`WeakReferenceMessenger`) — RED tests.
- [ ] 8.2 Wait dialog + local authorization dialog + retry with token (`SalesService.AddItemAsync` gains optional token header) — RED VM tests (countdown, unlock, error paths).
- [ ] 8.3 Admin notification dialog + race handling + `PosViewModel.Orders.cs` custom-price integration + dispatcher/dispose lifecycle — RED tests.

## Phase 9: Closure (T11)

- [ ] 9.1 Full verification: build 0/0; full suite; coverage gates Core .70 / Sales .80 / Inventory .72; `npm test` + lint; gated E2E if the environment allows; independent verifiers per work unit + final.
- [ ] 9.2 ANEXO 8.150 in `docs/reporte.txt` + verify-report + tracker close; push/PR/merge decision to the maintainer; sdd-archive after merge.

## Notes

- ODD tracker: `odd/tasks/autorizaciones-remotas-signalr.md` (statuses, evidence, log).
- RED→GREEN per task; evidence recorded in the tracker; commits `feat(8.150)` per task.
- Maintainer decisions (L6, 2026-10-07): cancellation gating VETOED (no behavior change for cashiers); Web manual-price UI VETOED (no Web-side trigger in v1); local fallback via real credentials CONFIRMED.
