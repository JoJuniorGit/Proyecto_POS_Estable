# Tasks: Authorization Residual Closeout (8.151)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~1,400–2,000 |
| Budget risk | Medium |
| Chained PRs | Yes (continues the V0.15 chain) |
| Split | 5 work units + closure |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Backend closeout (W1) | `--filter "FullyQualifiedName~Authorization"` + E1 filter with Postgres | Migration Down restores old index; revert service/controller changes |
| 2. Web closeout (W2) | `npm test` (Web.Frontend) | Revert web services/components |
| 3. WPF closeout (W3) | `--filter "FullyQualifiedName~AuthorizationNotification\|~AuthorizationWait"` | Revert WPF services/VMs |
| 4. WPF manual-price trigger (W4) | `--filter "FullyQualifiedName~ManualPrice\|~PosViewModel"` | Remove dialog + wiring |
| 5. Extras + closure (W5) | WPF-E2E gated filters + full suite + coverage | Revert tests/docs |

## Phase 1: Backend closeout (W1)

- [x] 1.1 New migration: pending partial unique index recreated as `NULLS NOT DISTINCT` (Npgsql-guarded); Down restores the previous definition; Postgres-gated test: duplicate `(user, NULL, action)` Pending rejected with 23505 and translated to `Deduplicated`.
- [x] 1.2 `TryConsumeAsync`: binding (action/sale/context) included in the atomic claim WHERE; post-`0-row` classification read preserves precise outcomes; tests for tampered/mismatched claims.
- [x] 1.3 Lazy-expiry loser re-reads before classifying `Expired`; test pins the concurrent-approval interleave (+ cancelled interleave, corrected post-verification).
- [x] 1.4 Cancellation: `AuthorizationStatus.Cancelled`; `CancelAsync` (atomic, requester-only, audited); coordinator push (Cancelled closure, no token); REST `POST /api/authorizations/{id}/cancel`; E1 scenario (cancel → Cancelled + audit + elevated closure; cannot cancel after approval/other requester).
- [x] 1.5 Spec qualifications landing in code where applicable (missing-key 400 precedes 403 on retry — pinned in E1).

## Phase 2: Web closeout (W2)

- [x] 2.1 Reducer settles the caller promise on rejection/expiry (`{ ok:false, outcome, reason }`); provider wiring; reducer tests pin the semantics and the cancel-vs-rejection distinction.
- [x] 2.2 `cancelWait` fires the server cancel (best-effort, non-blocking); test with fake client.
- [x] 2.3 Admin notifications: `Cancelled` status closes with "Solicitud cancelada por el cajero." (never the race message); tests.
- [x] 2.4 Countdown floor + own-resolved pruning; tests.

## Phase 3: WPF closeout (W3)

- [ ] 3.1 `AuthorizationHubService.Reconnected` event + notification queue reconciliation via `GetStatusAsync` (resolved/expired/cancelled); tests with fake hub.
- [ ] 3.2 `cancelWait` fires the server cancel (best-effort); `Cancelled` closure handling in the notification VM; tests.
- [ ] 3.3 Countdown floor + own-resolved pruning + initial focus capture on the notification dialog; tests where headless-possible.

## Phase 4: WPF manual-price trigger (W4)

- [ ] 4.1 "Precio manual" affordance in the product-suggestion flow + USD/Bs input dialog (validation; Bs derived from rate when omitted); VM/dialog tests.
- [ ] 4.2 PosViewModel wiring: manual-price add routes through the existing `AddItemAsync` (gate/wait flow for cashiers; direct for elevated); tests.

## Phase 5: Extras + closure (W5)

- [ ] 5.1 WPF-E2E gated `FullStackHubWebSocketTests`: real backend, transport negotiated = WebSockets, create/resolve roundtrip (LongPolling residual closed for the real transport).
- [ ] 5.2 PUT pin test: non-elevated custom price on `PUT /{id}/items` keeps the role-based 403 without the flow extensions (deliberate scope).
- [ ] 5.3 Final verification (full suite + coverage + web + E1 with the new cancel scenario + gated WebSockets) + ANEXO 8.151 + verify-report + tracker close.

## Notes

- Tracker: `odd/tasks/cierre-residuales-autorizaciones.md`.
- E2 (WPF admin-notification UI E2E) stays cancelled (stash@{0}); do not revive in this change.
- Commits `fix(8.151)` per work unit.
