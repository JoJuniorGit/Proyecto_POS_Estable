# Design: Web Hold-Lock UI Tests

## Technical Approach

Extract the OnHold claim/release lifecycle out of `PendingOrdersPage.jsx` into a pure, dependency-injected controller that the page wires to `salesApi` and its own setters. The harness (`node --test` + esbuild JSX loader, no jsdom) can only reach handlers and effects through such a module, because `renderToString` runs neither. Presentational `renderToString` tests then lock down the badge / disabled / "Liberar" contract. Zero new dependencies; observable page behavior is unchanged.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| 1 | Controller boundary | `src/utils/holdOrderLockController.js` exporting `createHoldOrderLockController({ claimSale, releaseSale, reload, onError })` → `start`, `releaseActive`, `forceRelease`, `getActiveLockId()` | Hook, class, page-internal refactor | Spec fixes this factory and these capabilities; a factory keeps the module React/DOM-free and injectable, sitting beside `holdLock.js` |
| 2 | How the modal opens | `start` resolves `Promise<boolean>` (`true` = claimed); the page opens the action modal | Inject `onClaimed`/setters into the factory | Factory deps are fixed at four. Opening differs per action (Checkout sets `selectedSaleForCheckout`; Editing also sets `selectedSaleId`), so UI stays in the page |
| 3 | Active-lock reconciliation | Not extracted; subsumed by best-effort release | Add a fifth `syncActiveLock(items)` method | `ReleaseSaleAsync` is idempotent: clearing an absent claim is a 200 no-op and a foreign lock 409s, which `releaseActive` swallows. Only delta is one redundant request in a stale-lock race, with no user-visible effect; a fifth method would deviate from the fixed contract and force a circular wiring (`reload` ⇄ controller) |
| 4 | Test harness | Controller unit tests with injected spies + `renderToString` presentational tests | jsdom / Testing Library click harness | jsdom is a new dependency, contradicts the loader's "renderToString, sin jsdom" design, and is explicitly deferred by proposal/exploration |
| 5 | Fixtures | Shared `Web.Frontend/test/pendingSaleFixture.js` builder | Inline per test | `test/` already hosts harness code; one builder supplies every field both components dereference and keeps the two suites consistent |

## Data Flow

```
DesktopRow/MobileCard ──onCheckout/onEdit──▶ page handler ──▶ controller.start(sale, action, currentUserId)
                                                                  │
                                              isLockedByOther? ────┤ true ─▶ return false (no claim)
                                                                  ▼
                                              onError(null); claimSale(id, action)
                                                                  │ ok ──▶ activeLockId = id ──▶ true ──▶ page opens modal
                                                                  │ err ─▶ reload() + onError(msg || default) ──▶ false
close / complete ──▶ controller.releaseActive() ──▶ releaseSale(activeLockId) ──▶ activeLockId = null ──▶ reload()
Liberar (Admin/Manager) ──▶ controller.forceRelease(sale) ──▶ releaseSale(id, true) ──▶ reload()
unmount ──▶ controller.releaseActive() (fire-and-forget; errors swallowed)
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Web.Frontend/src/utils/holdOrderLockController.js` | Create | Pure DI lifecycle controller |
| `Web.Frontend/src/utils/holdOrderLockController.test.js` | Create | Spy-based unit tests |
| `Web.Frontend/src/pages/PendingOrdersPage.jsx` | Modify | Delegate lifecycle; drop `activeLockRef`/`ignoreLockReleaseFailure` |
| `Web.Frontend/src/components/pending/PendingOrderDesktopRow.test.js` | Create | `renderToString` presentation tests |
| `Web.Frontend/src/components/pending/PendingOrderMobileCard.test.js` | Create | `renderToString` presentation tests |
| `Web.Frontend/test/pendingSaleFixture.js` | Create | Shared sale fixture builder |

## Interfaces / Contracts

```js
import { isLockedByOther } from './holdLock.js';

export function createHoldOrderLockController({ claimSale, releaseSale, reload, onError }) {
  let activeLockId = null;
  return {
    async start(sale, action, currentUserId) { /* guard → onError(null) → claimSale → track; on error reload + onError → false */ },
    async releaseActive() { /* releaseSale(activeLockId), clear on success, swallow errors */ },
    async forceRelease(sale) { /* onError(null) → releaseSale(sale.id, true) → clear if same → reload; error → onError */ },
    getActiveLockId() { return activeLockId; },
  };
}
```

`start` returns `true` only after `claimSale` resolves. Default messages: `'No se pudo reclamar el pedido.'` (start) and `'No se pudo liberar el pedido.'` (forceRelease). Page wiring: `useMemo(() => createHoldOrderLockController({ claimSale, releaseSale, reload: loadPendingData, onError: setError }), [loadPendingData])`; `useEffect` cleanup calls `releaseActive()`; the two `onCompleteSale` release sites and both close handlers call `releaseActive()`; `handleForceRelease` calls `forceRelease(sale)`. Cancellation is unchanged (server-side `ClearHoldClaim`).

## Testing Strategy

| Layer | What | Approach |
|-------|------|----------|
| Unit | Controller lifecycle | Injected spies; assert call args, return value, internal id |
| Render | Badge / disabled / title / "Liberar" | `renderToString`; assert markup substrings |

Controller (`Metodo_Escenario_ResultadoEsperado`): `start_ConPedidoLibreYAccionCheckout_ReclamaYDevuelveVerdadero`, `start_ConAccionEditing_ReclamaConEditing`, `start_ConBloqueoAjeno_NoReclamaYDevuelveFalso`, `start_CuandoElReclamoFalla_RecargaYPropagaElMensaje`, `start_CuandoFallaSinMensaje_UsaMensajePorDefecto`, `start_AlIniciar_LimpiaElError`, `releaseActive_ConBloqueoActivo_LiberaYOlvidaElId`, `releaseActive_SinBloqueoActivo_NoLlamaRelease`, `releaseActive_CuandoFalla_NoPropagaError`, `forceRelease_ConBloqueoAjeno_LiberaConForceYRecarga`, `forceRelease_CuandoFalla_PropagaElMensaje`, `forceRelease_DelPropioBloqueoActivo_OlvidaElId`. Render (per desktop row and mobile card): `*_ConBloqueoAjeno_MuestraBadgeYDeshabilitaAcciones`, `*_ConBloqueoPropio_MuestraBloqueadoPorTiYHabilitaAcciones`, `*_SinReclamo_NoMuestraBadgeYHabilitaAcciones`, `*_ConAdminYBloqueoAjeno_MuestraLiberar`, `*_ConCajeroYBloqueoAjeno_NoMuestraLiberar`. Fixtures include `id`, `date`, `customerName`, `customerCedula`, `items`, `payments`, `totalBsS`, `totalUSD`; `lockInfo` comes from the real `getLockInfo`. Commands: `npm test`, `npm run lint`.

## Threat Matrix

N/A — no routing, shell, subprocess, VCS/PR automation, executable-file classification, or process-integration boundary.

## Migration / Rollout

No migration. `rules.md`/money untouched: no snapshots, `decimal`, or EF changes. Rollback: `git revert` the extraction and test commits; no schema or data.

## Open Questions

- None blocking. Decision 3 (reconciliation subsumption) is the only behavioral nuance; revisit only if a future page-level test demands exact request parity.
