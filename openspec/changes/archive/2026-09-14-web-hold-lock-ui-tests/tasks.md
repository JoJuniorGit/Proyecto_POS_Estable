# Tasks: Web Hold-Lock UI Tests

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | 200–280 |
| 400-line budget risk | Low |
| Chained PRs recommended | No |
| Suggested split | Single PR |
| Delivery strategy | ask-on-risk |
| Chain strategy | pending |

Decision needed before apply: No
Chained PRs recommended: No
Chain strategy: pending
400-line budget risk: Low

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | Controller + tests + fixture + page wiring + render tests | Single PR | `npm test && npm run lint` | `node --test` with esbuild JSX loader | `git revert` the extraction+test commit |

## Phase 1: Controller Extraction

- [x] 1.1 Create `Web.Frontend/src/utils/holdOrderLockController.js` — export `createHoldOrderLockController({ claimSale, releaseSale, reload, onError })` returning `start(sale, action, currentUserId)`, `releaseActive()`, `forceRelease(sale)`, `getActiveLockId()`. `start` calls `isLockedByOther` guard → `onError(null)` → `claimSale(id, action)` → track `activeLockId` → return `true`; on error: `reload()` + `onError(msg || 'No se pudo reclamar el pedido.')` → return `false`. `releaseActive`: `releaseSale(activeLockId)` → clear on success, swallow error. `forceRelease`: `onError(null)` → `releaseSale(sale.id, true)` → clear if same → `reload()`; error → `onError(msg || 'No se pudo liberar el pedido.')`. Spec: Req 6 (testable controller), Req 1-3 (claim/release), Req 5 (force-release).

## Phase 2: Controller Tests

- [x] 2.1 Create `Web.Frontend/src/utils/holdOrderLockController.test.js` — spy-based unit tests. Cover: `start_ConPedidoLibreYAccionCheckout_ReclamaYDevuelveVerdadero` (Spec Req 1 Sc1), `start_ConAccionEditing_ReclamaConEditing` (Req 1 Sc1), `start_ConBloqueoAjeno_NoReclamaYDevuelveFalso` (Req 6 Sc2), `start_CuandoElReclamoFalla_RecargaYPropagaElMensaje` (Req 2 Sc2), `start_CuandoFallaSinMensaje_UsaMensajePorDefecto` (Req 2 Sc2), `start_AlIniciar_LimpiaElError` (Req 2), `releaseActive_ConBloqueoActivo_LiberaYOlvidaElId` (Req 3 Sc1), `releaseActive_SinBloqueoActivo_NoLlamaRelease` (Req 3 Sc1), `releaseActive_CuandoFalla_NoPropagaError` (Req 3 Sc3), `forceRelease_ConBloqueoAjeno_LiberaConForceYRecarga` (Req 5 Sc1), `forceRelease_CuandoFalla_PropagaElMensaje` (Req 5), `forceRelease_DelPropioBloqueoActivo_OlvidaElId` (Req 5).

## Phase 3: Shared Fixture

- [x] 3.1 Create `Web.Frontend/test/pendingSaleFixture.js` — export `buildPendingSale(overrides)` returning sale object with all fields both components dereference: `id`, `date`, `customerName`, `customerCedula` (or `customer.cedulaOrRif`), `customer`, `items` (array with `id`, `productName`, `quantity`, `unitPriceBsS`, `subtotalBsS`), `payments` (array with `id`, `paymentMethodName`, `amountBsS`, `amount`, `exchangeRate`, `createdAt`), `totalBsS`, `totalUSD`, `totalPaidUSD`, `remainingBalanceUSD`, `claimedByUserId`, `claimedByUserName`, `claimAction`. Builder lives outside `src/**` so `node --test` glob ignores it. Spec: Req 6 (testable controller).

## Phase 4: Page Wiring

- [x] 4.1 Modify `Web.Frontend/src/pages/PendingOrdersPage.jsx` — replace inline `activeLockRef`/`ignoreLockReleaseFailure` with `useMemo(() => createHoldOrderLockController({ claimSale, releaseSale, reload: loadPendingData, onError: setError }), [loadPendingData])`. Wire `handleStartCheckout` to `controller.start(sale, 'Checkout', currentUserId)`, `handleEditSale` to `controller.start(sale, 'Editing', currentUserId)`, close/complete handlers to `controller.releaseActive()`, `handleForceRelease` to `controller.forceRelease(sale)`. Add `useEffect` cleanup calling `controller.releaseActive()`. Preserve all observable behavior (modal opens, error messages, list reload). Spec: Req 6 (lifecycle runs headlessly), Req 1-5.

## Phase 5: Presentational Tests

- [x] 5.1 Create `Web.Frontend/src/components/pending/PendingOrderDesktopRow.test.js` — `renderToString` tests with fixture. Cover: `PendingOrderDesktopRow_ConBloqueoAjeno_MuestraBadgeYDeshabilitaAcciones` (Spec Req 4 Sc1), `PendingOrderDesktopRow_ConBloqueoPropio_MuestraBloqueadoPorTiYHabilitaAcciones` (Req 4 Sc2), `PendingOrderDesktopRow_SinReclamo_NoMuestraBadgeYHabilitaAcciones` (Req 4 Sc2), `PendingOrderDesktopRow_ConAdminYBloqueoAjeno_MuestraLiberar` (Req 5 Sc1), `PendingOrderDesktopRow_ConCajeroYBloqueoAjeno_NoMuestraLiberar` (Req 5 Sc2).

- [x] 5.2 Create `Web.Frontend/src/components/pending/PendingOrderMobileCard.test.js` — `renderToString` tests with fixture. Cover: `PendingOrderMobileCard_ConBloqueoAjeno_MuestraBadgeYDeshabilitaAcciones` (Spec Req 4 Sc1), `PendingOrderMobileCard_ConBloqueoPropio_MuestraBloqueadoPorTiYHabilitaAcciones` (Req 4 Sc2), `PendingOrderMobileCard_SinReclamo_NoMuestraBadgeYHabilitaAcciones` (Req 4 Sc2), `PendingOrderMobileCard_ConAdminYBloqueoAjeno_MuestraLiberar` (Req 5 Sc1), `PendingOrderMobileCard_ConCajeroYBloqueoAjeno_NoMuestraLiberar` (Req 5 Sc2).

## Phase 6: Verification & Rollback

- [x] 6.1 Run `npm test` in `Web.Frontend` — all tests green (existing 156 + new controller + render tests).
- [x] 6.2 Run `npm run lint` in `Web.Frontend` — 0 errors.
- [x] 6.3 Verify no behavior drift: checkout/edit modals open only after claim resolves; error banner shows server message on 409; release on close/complete is fire-and-forget; force-release only for Admin/Manager on foreign lock.
- [x] 6.4 Rollback plan: `git revert` the extraction commit and test commit; no migrations, no data changes.
