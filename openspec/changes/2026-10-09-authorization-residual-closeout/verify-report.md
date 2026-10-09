# Verify Report: Authorization Residual Closeout (8.151)

## Verdict

**PASS** — every residual registered for 8.150 (ANEXO 8.150 §D, tracker L5-L16) is closed by code, tests and evidence. Recorded LOWs are enumerated below. E2 (WPF admin-notification UI E2E) remains cancelled by the maintainer (stash preserved) — out of this change by decision.

## Final gates (closure)

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 warnings / 0 errors**.
- Full suite WITH real PostgreSQL: **2109/2109**, 0 skipped (2m18s) — includes the 7 gated full-stack E2E scenarios (incl. the new cancellation one), all Postgres-gated tests and the PUT pins.
- Coverage: Core **0.8868** / Sales.Module **0.9055** / Inventory.Module **0.8587** vs gates .70/.80/.72 — `check-coverage.py` exit 0 (improved vs 8.150).
- Web: `npm test` **373/373**; `npm run lint` 0 findings.
- Gated WPF E2E WebSockets test (real Kestrel + PostgreSQL): **1/1 in 19 s — re-executed by the parent** with a fail-closed transport assertion (requires `Starting transport 'WebSockets'` and no other transport start lines).
- PUT scope pins: **10/10** (`~ProtectedAction`), incl. with/without `X-Authorization-Token` → same role-based 403, no extensions, no mutation.
- Leftovers: `pos_e2e%` = 0; the W1 scratch DB `pos_test_w1` was **dropped** at closure (verified 0).

## Residual closure matrix (8.150 → 8.151)

| 8.150 residual | Status | Evidence |
|---|---|---|
| D1/R3 NULL-sale dedupe gap | CLOSED | `20261010120000_PendingDedupeNullSafe` (NULLS NOT DISTINCT, Npgsql) + gated 23505→Deduplicated test |
| R1 consume binding pre-read TOCTOU | CLOSED | binding in the atomic claim WHERE + 0-row classification; theory tests |
| R2 lazy-expiry loser misclassification | CLOSED | re-read in both resolve paths + interleave tests (+ cancelled-interleave fixed post-verification) |
| D4b cancel leaves request Pending | CLOSED | `Cancelled=4`, requester-only atomic `POST /{id}/cancel`, single audit, elevated closure push (no token), E1 scenario |
| D3/W3 web rejection promise | CLOSED | settlement semantics per spec + reducer tests |
| D4a WPF reconnect reconciliation | CLOSED | `Reconnected` event + full queue re-sync via `GetStatusAsync` |
| D4c ceiling countdown | CLOSED | floor in web + both WPF VMs (+tests) |
| D4d own-resolved unbounded | CLOSED | bounded prune + consume-on-push (web + WPF) |
| D2 LongPolling-only | CLOSED | gated real-WebSockets E2E (negotiation asserted fail-closed) |
| D5 PUT parity | CLOSED BY DESIGN | deliberate-scope pin tests (no extensions, token ignored) |
| D6/G1 no reachable UI trigger | CLOSED | WPF "Precio manual" (F6 + popup button, validation + derivation) through the same gate/wait route |
| R11 400-vs-403 qualification | CLOSED | spec qualified + E1 pin (consumed token w/o key → 400) |
| E2 (cancelled E2E) | UNCHANGED | WIP in `stash@{0}` (maintainer decision) |

## Findings/notes (honest)

- W1: independent verifier PASS WITH WARNINGS; its LOW (cancelled-concurrent misclassified as Expired) was **fixed test-first** and re-verified (RED 2F → GREEN 252/252).
- W2: independent verifier PASS WITH WARNINGS; LOWs recorded (consume-on-push/reconcile static-only; degradation-retry invisible edge).
- W3: independent verifier PASS (2 LOWs recorded).
- **W4/W5: the independent verifier run was interrupted by the maintainer (pace decision); verification of record = parent spot-checks** (W4: diff review of the untouched cash-advance branch, catch/call-site confirmed, 32/32 combined filter; W5: PUT 10/10 + WebSockets test re-executed 1/1 on the real stack). No blocking finding observed.
- Local `pos_test` remains stale (missing 8.150 tables; bootstrap marker check limitation) — pre-existing local artifact, NOT repaired here; gated runs use fresh scratch DBs.

## Artifacts / commits

Change `2026-10-09-authorization-residual-closeout` (proposal, design, specs, tasks, state, this report); tracker `odd/tasks/cierre-residuales-autorizaciones.md`; ANEXO 8.151. Commits: `a944952` (plan) + W1 `7fa0d6c` + W2 `387bcf3` + W3 `3eae78e` + W4 `93c72ea` + W5 `a33a8a7` + docs commits + closure.
