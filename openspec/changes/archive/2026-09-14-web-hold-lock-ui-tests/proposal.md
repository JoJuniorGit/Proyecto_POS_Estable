# Proposal: Web Hold-Lock UI Tests

## Intent

Close ANEXO 8.121 E1: the web claim/release lifecycle of OnHold sales (`PendingOrdersPage.jsx`) and its lock UI are untested. The harness is `renderToString`-only (no jsdom), so effects and handlers — where the lifecycle lives — are unreachable. Behavior stays; verifiability and its spec contract are added.

## Scope

### In Scope
- Extract the lifecycle into a pure, DI controller at `Web.Frontend/src/utils/holdOrderLockController.js`.
- Wire `PendingOrdersPage.jsx` to it (behavior-preserving).
- Spy-based `holdOrderLockController.test.js`.
- `renderToString` tests for badge/disabled/"Liberar" on `PendingOrderDesktopRow` and `PendingOrderMobileCard`.

### Out of Scope
- jsdom / click-level harness (future change; also unblocks 8.120 E2).
- Backend, WPF, `salesApi`/`holdLock` contract changes, new dependencies.

## Capabilities

### New Capabilities
- `pending-order-hold-lock`: web claim/release lifecycle (guard, claim, release, force-release, 409 recovery) plus lock presentation (badge, disabled actions, elevated "Liberar").

### Modified Capabilities
- None.

## Approach

`createHoldOrderLockController({ claimSale, releaseSale, reload, onError })` exposes `start(sale, action, currentUserId)`, `releaseActive()`, `forceRelease(sale)` and `getActiveLockId()`. `start` guards with `isLockedByOther`, claims, tracks the active id; on failure it reloads and propagates `err.message`. The page injects the real `salesApi` functions and its setters. Presentational tests build fixtures with every field the components read and assert `Metodo_Escenario_ResultadoEsperado`.

**Affected project**: `Web.Frontend` → `npm test`, `npm run lint`.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Web.Frontend/src/utils/holdOrderLockController.js` | New | DI lifecycle controller |
| `Web.Frontend/src/pages/PendingOrdersPage.jsx` | Modified | delegates lifecycle |
| `Web.Frontend/src/utils/holdOrderLockController.test.js` | New | spy tests |
| `Web.Frontend/src/components/pending/*.test.js` | New | render tests |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Behavior drift in extraction | Med | Byte-for-behavior port; web suite stays green |
| Invalid fixtures throw on render | Med | Fixtures include `date`, `customer*`, `items`, `payments`, `totalBsS`, `totalUSD` |
| Review budget | Low | Test-focused; under 400 lines |

## Rollback Plan

`git revert` the extraction commit and the test commit; no migrations, no data, no persisted contract changes.

## Dependencies

None — zero new packages.

## Success Criteria

- [ ] New tests pass; `Web.Frontend` suite green (156 + new); `npm run lint` 0 errors.
- [ ] Scenarios covered: claim OK (`Checkout`/`Editing`), 409 by other → message + reload, release on close/complete, never release a foreign lock, force-release only when elevated.

## Strict TDD

`strict_tdd: false` (`openspec/config.yaml`); per-project commands, no workspace-wide runner.
