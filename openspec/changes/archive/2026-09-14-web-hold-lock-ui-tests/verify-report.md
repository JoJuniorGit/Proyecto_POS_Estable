```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:511c9412841a333531ae054a0998481c7f7ea26f89b0f0b5763ca6b536d729d2
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 6/6
scenarios: 11/11
test_command: npm test
test_exit_code: 0
test_output_hash: sha256:df0e5e869eed941f2814510db9443380b7869563fcf87814c5c5212ef61b5b96
build_command: npm run build
build_exit_code: 0
build_output_hash: sha256:726565377981bd33d55d9770a48d458429fc9a52a16c1f35c68d860cb0871492
```

# Verify Report: web-hold-lock-ui-tests

Scope: `Web.Frontend`. Commands re-executed independently by the verifier (not trusted from apply).
`evidence_revision` is the SHA-256 of `git diff --cached -- Web.Frontend` at verification time.

## Commands

| Command | Exit | Result | Output hash |
|---------|------|--------|-------------|
| `npm test` | 0 | tests 178 / suites 39 / pass 178 / fail 0 (baseline 156 + 22 new: 12 controller + 5 desktop + 5 mobile) | `sha256:df0e5e86...61b5b96` |
| `npm run lint` | 0 | 0 errors (oxlint) | `sha256:771a06ed...e1101fa` |
| `npm run build` | 0 | vite built 2146 modules | `sha256:72656537...0871492` |

`npm run lint` and `npm run build` are supplementary checks; the envelope records `npm run build` as the build/type-check evidence. The build rewrote tracked `Backend.API/wwwroot` assets as a side effect; the pre-verification tree was restored via `git checkout -- Backend.API/wwwroot` and `git clean -fd Backend.API/wwwroot` (verified: `git status` matches the pre-verification staged set).

## Requirement Verdicts (6/6 pass, 11/11 scenarios pass)

| # | Requirement | Scenarios | Status | Evidence |
|---|-------------|-----------|--------|----------|
| 1 | Claim Before Opening an Action Modal | 1/1 | pass | `holdOrderLockController.js:11` `claimSale(sale.id, action)`; `PendingOrdersPage.jsx:114-125` opens modal only after `start` resolves `true`; tests `start_ConPedidoLibreYAccionCheckout_ReclamaYDevuelveVerdadero`, `start_ConAccionEditing_ReclamaConEditing` |
| 2 | Claim Conflict (409) | 1/1 | pass | `holdOrderLockController.js:14-18` `reload()` + `onError(err.message \|\| default)` + `return false`; `PendingOrdersPage.jsx:116` skips modal when false; tests `start_CuandoElReclamoFalla_RecargaYPropagaElMensaje`, `start_CuandoFallaSinMensaje_UsaMensajePorDefecto` |
| 3 | Best-Effort Lock Release | 3/3 | pass | `holdOrderLockController.js:21-29` `releaseSale(activeLockId)` then clear, `catch {}` swallows; `PendingOrdersPage.jsx:127-137` close handlers release+reload, `:336` and `:378` completion release; tests `releaseActive_ConBloqueoActivo_LiberaYOlvidaElId`, `releaseActive_SinBloqueoActivo_NoLlamaRelease`, `releaseActive_CuandoFalla_NoPropagaError` |
| 4 | Foreign Lock Presentation and Protection | 2/2 | pass | `PendingOrderDesktopRow.jsx:46-62` and `PendingOrderMobileCard.jsx:63-80` set `disabled={lockInfo.isLockedByOther}` + `title={lockInfo.label}`; `PendingOrderLockBadge.jsx:8` renders `lockInfo.label`; tests `*_ConBloqueoAjeno_MuestraBadgeYDeshabilitaAcciones`, `*_ConBloqueoPropio_MuestraBloqueadoPorTiYHabilitaAcciones`, `*_SinReclamo_NoMuestraBadgeYHabilitaAcciones` |
| 5 | Elevated Force-Release | 2/2 | pass | `holdOrderLockController.js:31-40` `forceRelease` -> `releaseSale(sale.id, true)` + `reload()`; components render Liberar only when `isElevated && isLockedByOther`; `PendingOrdersPage.jsx:31` `isElevated = role Admin\|Manager`; tests `*_ConAdminYBloqueoAjeno_MuestraLiberar`, `*_ConCajeroYBloqueoAjeno_NoMuestraLiberar`, `forceRelease_ConBloqueoAjeno_LiberaConForceYRecarga` |
| 6 | Testable Controller with Injected Dependencies | 2/2 | pass | `holdOrderLockController.js:3` factory `{ claimSale, releaseSale, reload, onError }`; imports only `./holdLock.js` (no React/DOM); `PendingOrdersPage.jsx:63-66` delegates; tests `start_ConBloqueoAjeno_NoReclamaYDevuelveFalso` and full spy lifecycle |

## Change Hygiene Checks

| Check | Result | Evidence |
|-------|--------|----------|
| New dependencies | pass | `Web.Frontend/package.json` unchanged; imports only existing packages (`node:test`, `node:assert`, `react`, `react-dom/server`, existing utils) |
| New inline styles | pass | New test files are plain JS; `PendingOrderDesktopRow.jsx` / `PendingOrderMobileCard.jsx` are not in the diff |
| New comments | warning | One new explanatory comment at `holdOrderLockController.js:27` (`// best-effort: swallow release failures`) deviates from AGENTS.md rule 1 |
| Observable behavior preserved | pass | Full `PendingOrdersPage.jsx` diff reviewed; only delta is removal of the stale active-lock reconciliation on reload, subsumed by idempotent best-effort release (design decision 3) with no user-visible effect |

## Warnings and Observations (non-blocking)

- W1: New explanatory comment `holdOrderLockController.js:27`; AGENTS.md rule 1 forbids new explanatory comments. Cosmetic, non-behavioral.
- W2: In-scope `Web.Frontend` diff is 447 changed lines (394 insertions / 53 deletions), above the 400-line review-policy budget. Review state is informational and never a verification prerequisite.
- W3: Render tests assert a generic `/disabled/` and the badge name substring only; the full badge `" - <accion>"` label is covered by the pre-existing `holdLock.test.js` (`getLockInfo_ConVentaReclamadaPorOtroEnCheckout_IncluyeNombreYAccion`), not by the row/card tests.
- W4: Requirement 3 cancellation scenario relies on server-side `ClearHoldClaim` (`cancelSale`) rather than `controller.releaseActive()`; outcome (lock released, no user-visible error) holds and matches design decision 3.
- W5: `opencode.json` is staged with agent model changes unrelated to this SDD change; model selection is user-owned and was left untouched.

No code was edited during verification. Global verdict: **pass_with_warnings**.
