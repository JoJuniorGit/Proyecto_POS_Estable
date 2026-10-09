# Verify Report: Remote Authorization Hub (Paso 4 / 8.150)

## Verdict

**PASS WITH WARNINGS** — 11/12 spec requirements COMPLIANT + 1 PARTIAL (WPF notification UI E2E cancelled by the maintainer); 0 CRITICAL findings. The single blocking finding of the final pass was a **test-authoring defect** (not product behavior), fixed and re-verified.

## Final gates (independently run)

- Build: `dotnet build CommandCenter.slnx -c Release` → 0 warnings / 0 errors.
- Full suite WITH real PostgreSQL (`TEST_POSTGRES_CONNECTION`): **2038/2038**, 0 skipped (2m13s) — includes the 6 gated full-stack E2E scenarios.
- Full-stack E2E (real Postgres + real SignalR + real HTTP/JWT): **6/6** (~34s): HappyFullJourney, Race, LocalFallback, Expiry (real hosted job), AuditImmutability (P0001), TokenBinding. Zero `pos_e2e%` leftovers.
- Coverage: Core **0.8624** / Sales.Module **0.8844** / Inventory.Module **0.8342** vs gates .70/.80/.72 — `check-coverage.py` exit 0.
- Web: `npm test` **361/361**; `npm run lint` 0 findings.
- Corrections spot-verified in-tree: T5 real-value binding, T10 Notice-before-Remove, 403 status gate.

## Compliance matrix

| Spec | Requirement | Verdict |
|---|---|---|
| remote-authorization | Creation via hub/REST + dedupe + enriched broadcast | COMPLIANT |
| remote-authorization | Race-safe resolution + exact message | COMPLIANT |
| remote-authorization | Ephemeral single-use token + recovery window | COMPLIANT |
| remote-authorization | Expiry 60 s (lazy + sweep + push) | COMPLIANT |
| remote-authorization | Local fallback + lockout + fail-closed | COMPLIANT |
| remote-authorization | Immutable audit + Postgres trigger | COMPLIANT (post-fix) |
| remote-authorization | Protected AddItem gate (ManualPriceOverride) | COMPLIANT |
| remote-authorization-clients | Blocking wait state (Web infra + WPF) | COMPLIANT |
| remote-authorization-clients | Enriched admin notification Web + WPF | PARTIAL (real-WPF push E2 cancelled) |
| remote-authorization-clients | Rejection / expiry outcomes | COMPLIANT |
| remote-authorization-clients | Local authorization from terminal | COMPLIANT |
| remote-authorization-clients | WPF hub lifecycle / thread affinity | COMPLIANT |

S7 (generic layer for Pasos 5/11) — met structurally (enum registry + coordinator switch; no second consumer yet by design).

## Final-verification findings and corrections

- **B1 (blocking, test-only)**: `AuthorizationPersistenceTests.cs:317` asserted `Reason == null` while its fixture creates the audit with `"Precio acordado"` — could never pass with `TEST_POSTGRES_CONNECTION` set (latent since T1, hidden by the early-return gating; would have failed CI's backend job). Fixed to assert the unchanged value (row-unchanged intent) → focused Postgres 6/6, full suite green. Commit `f290384`.
- **WPF full-stack smoke**: NOT passed in this environment — `Login_SubmitButton no está presente` (`TestHelper.cs:73`, unretried UIA probe). Discriminated as **environment/harness, not a regression**: the WPF app boots to "INICIO DE SESIÓN" outside FlaUI with the same Release binary; `LoginView` untouched by the 8.150 chain; CI's `wpf-e2e` job (windows-2025, `E2E_FIND_TIMEOUT_SECONDS=45`) covers it.

## Per-unit independent verification

T1-T10 + E1 each verified (PASS / PASS WITH WARNINGS, 0 blocking). Post-verification corrections, each re-verified: T2-W2 (non-disclosure lockout message), T5 circular binding (real request sale/user + cross-sale test), T7-W1/W2 (single-flight retry + retry recovery), T10 notice-ordering (exact race message lost on single-request drain), T11 trigger-assert fix. **E2** (WPF admin notification UI E2E) was **cancelled by the maintainer** — WIP stashed (`stash@{0}`), not delivered.

## Accepted residuals

`(user,NULL,action)` concurrent index gap (service-owned dedupe); LongPolling-only hub tests; web provider untestable in no-DOM runner (W3 rejection not resolving caller promise — no v1 web caller); WPF notification queue without reconnect reconciliation; cancel leaves the server request Pending until expiry; ceiling countdown; `PUT /{id}/items` keeps its role-based gate without the authorization flow; no web manual-price trigger (veto L6); WPF UI has no successful protected-action trigger (cash-advance path SEC-02-rejected after approval).

## Artifacts

Change: `openspec/changes/2026-10-07-remote-authorization-hub/`; tracker: `odd/tasks/autorizaciones-remotas-signalr.md`; closure ANEXO 8.150 in `docs/reporte.txt`; commit chain `0da2c57` → `f290384`.
