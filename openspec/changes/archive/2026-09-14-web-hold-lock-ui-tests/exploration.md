## Exploration: web-hold-lock-ui-tests

### Current State

`Web.Frontend` tests run on `node --test` with a custom esbuild JSX loader; there is **no jsdom** and no testing library.

- `Web.Frontend/package.json:9` — `test` = `node --import ./test/esbuild-jsx-loader.mjs --test "src/**/*.test.js"`.
- `Web.Frontend/package.json:13-27` — deps are `react`, `react-dom`, `lucide-react`, `@microsoft/signalr`, `@zxing/library`; devDeps only `esbuild`, `oxlint`, `vite`, `@vitejs/plugin-react` and `@types/*`. No `jsdom`, `@testing-library/*`, `happy-dom` or `linkedom` (verified: none present in `Web.Frontend/node_modules`).
- `Web.Frontend/test/esbuild-jsx-loader.mjs:1-9` + `test/esbuild-jsx-loader-hook.mjs:36-72` — transpiles `.jsx` and JS-with-JSX via esbuild; CSS imports resolve to empty modules (`:47-49`) so components that import CSS can mount; extensionless imports resolved (`:12-32`). The header comment states the intent is **"montaje REAL de componentes React sin jsdom (renderToString)"** (`esbuild-jsx-loader.mjs:2-3`).
- Node is not pinned in `package.json`; CI uses Node `22.x` (`.github/workflows/ci.yml:109-111`); local runtime is Node `v24.11.1`.
- Component "mount" tests use `renderToString` from `react-dom/server`: `src/components/checkout/CheckoutModal.mount.test.js:8,19`, `src/pages/HistoryPage.mount.test.js:3,12`, `src/components/pos/BarcodeScannerRetry.test.js:98-147` (asserts conditional branches by props with regex on HTML).
- Service tests mock `global.fetch` and assert request shape: `src/services/salesApi.claim.test.js:8-26` (captures `global.__lastFetch`), covering `claimSale`/`releaseSale` URL+method+body.
- Hook/util logic is tested as **pure exported functions**, never by rendering a hook: `src/hooks/useShutdownGuard.test.js:3-11` imports `handleBeforeUnloadEvent`/`handleShutdownSequence`/`shouldRunShutdownOnPageHide`; `src/utils/holdLock.test.js:1-76` imports `isLockedByOther`/`getLockInfo`/`CLAIM_ACTION_LABELS`. (`src/hooks/usePosModalFlow.test.js:5-23` is a weaker anti-pattern that re-declares the logic locally instead of importing it.)

**What `renderToString` can and cannot do (no jsdom):**
- CAN: run the synchronous initial render of a component tree; assert rendered HTML/markup, conditional branches driven by props, `disabled`/`title` attributes, text/labels, provider composition.
- CANNOT: run `useEffect`/`useLayoutEffect` (mount/unmount side effects), execute event handlers (clicks), perform state updates after `await`, read `document`/`window` DOM, or drive real interactions. React 19 removed `react-test-renderer`, so a hook cannot be executed without a real DOM/renderer.
- Consequence: the claim/release lifecycle (which lives in **event handlers** and a **cleanup effect**) is unreachable via `renderToString`.

**No existing test imports `PendingOrdersPage` or the pending components** (grep over `*.test.js` returned zero matches). This is the exact E1 gap.

### Affected Areas

- `Web.Frontend/src/pages/PendingOrdersPage.jsx` — owns the whole claim/release lifecycle (487 lines).
  - Claim guard + `claimSale`: `handleStartCheckout` `:130-141` (`isLockedByOther` guard `:131`, `claimSale(sale.id,'Checkout')` `:134`, `activeLockRef.current = sale.id` `:135`, on failure `loadPendingData()` + `setError(err.message||…)` `:137-140`); `handleEditSale` `:143-155` mirrors it with `'Editing'`.
  - Release on close: `handleCloseCheckout` `:157-161`, `handleCloseEdit` `:163-167` → `releaseActiveLock` `:119-128` (swallows errors, clears ref).
  - Release on unmount: cleanup `useEffect` `:106-112` calls `releaseSale(activeLockRef.current)`.
  - Release on complete/partial payment: `onCompleteSale` handler `:361-434` calls `releaseActiveLock()` at `:373` and `:415`.
  - Release via cancel: `handleConfirmCancelSale` `:187-206` (backend clears claim through `ClearHoldClaim`).
  - Force release (Admin/Manager): `handleForceRelease` `:169-178` (`releaseSale(sale.id, true)` `:172`, clears ref if same, reload, error banner `:176`).
  - Derived state: `isElevated` `:34`; `getLockInfo(selectedSale, currentUserId)` `:184`; `selectedSaleLockedByOther` `:185`.
  - Error banner rendering: `:274-278`.
- `Web.Frontend/src/components/pending/PendingOrderLockBadge.jsx:3-10` — badge only when `lockInfo.isLocked`, label + `--mine` class.
- `Web.Frontend/src/components/pending/PendingOrderDesktopRow.jsx:20` (badge), `:46-73` (Cobrar/Editar `disabled={lockInfo.isLockedByOther}` `:49,:58`; "Liberar" gated by `isElevated && lockInfo.isLockedByOther` `:64-73`).
- `Web.Frontend/src/components/pending/PendingOrderMobileCard.jsx:23` (badge), `:63-98` (same gating; "Liberar Pedido" `:89-98`).
- `Web.Frontend/src/utils/holdLock.js` — pure `isLockedByOther` `:6-10`, `getLockInfo` `:12-28`, `CLAIM_ACTION_LABELS` `:1-4`. Already fully covered by `holdLock.test.js`.
- `Web.Frontend/src/services/salesApi.js` — `claimSale` `:207-209`, `releaseSale` `:211-213`. Contract already covered by `salesApi.claim.test.js`.
- `Web.Frontend/src/services/api.js:300-353` — maps non-ok responses to `throw new Error(errorMessage)` from body `message`/`Message`/`detail` (`:331-340`); so a 409 claim body's `message` becomes `err.message`, which the page surfaces verbatim. Backend 409 body shape is documented in `docs/reporte.txt:8055-8057` (8.121 E2) and originates in `Sales.Module/Services/SalesService.HoldClaims.cs:145-146` (`BuildSaleLockedException`).
- `Web.Frontend/src/context/AuthContext.jsx:28-38` normalizes the user to `{id,name,cedula,role}`; `useAuth` `:150-156` throws outside a provider. The page reads `user?.id`/`user?.role` (`PendingOrdersPage.jsx:33-34`), so `isElevated` depends on provider state loaded from `localStorage`.

### Approaches

1. **Add `jsdom` + a DOM harness and drive real clicks** (option a).
   - Pros: true component behavior — click → claim → modal open, disabled buttons, error banner, mount/unmount release; aligns with the documented ambition (`docs/plan-refactorizacion.md:319`).
   - Cons: new devDependency; needs global `window`/`document`/`navigator` setup for `node:test`; without `@testing-library/react` you must hand-dispatch events and wrap in `act`/flush microtasks, which is brittle; global DOM leakage across `node --test` files risks flakiness; CI Node 22 vs local 24 to validate; deviates from the loader's explicit "renderToString, sin jsdom" design.
   - Effort: High.

2. **Extract the claim/release lifecycle into a pure, DI helper and test it directly** (option b), keeping `renderToString` for the render surface.
   - Pros: zero new deps; deterministic and fast; matches the repo idiom (`useShutdownGuard` pure helpers, `holdLock`, `idempotency` holder); lets the page drop below its 487 lines (anti-god-object guidance §1.1); covers claim action, guard, release, force-release and 409 error mapping as logic.
   - Cons: not literally "click-level"; modal-open state (`setState`) is still not asserted; requires a small production refactor (not test-only).
   - Effort: Medium.

3. **Hybrid (recommended): option 2 + `renderToString` presentational tests, defer jsdom to a dedicated harness change.**
   - Pros: covers all five 8.121 scenarios with the smallest, lowest-risk change; keeps the existing zero-dependency harness intact; presentational tests lock the badge/disabled/"Liberar" contract.
   - Cons: the page itself still has no mount test; the granularity is "helper behavior + component render", not full interaction.
   - Effort: Medium.

4. **Lighter DOM (`linkedom`/`happy-dom`)** — still a new dependency, less standard than jsdom, same manual-`act` brittleness. Not worth it over option 3.

### Recommendation

Adopt **option 3**, delivered as a small, test-focused change:

1. Extract the lifecycle from `PendingOrdersPage.jsx` into a pure, dependency-injected module (e.g. `src/utils/holdOrderLockController.js`): `createHoldOrderLockController({ claimSale, releaseSale, reload, onError })` returning `start(sale, action, currentUserId)` (guard → `claimSale` → track active lock; on failure `reload()` + `onError(message)`), `releaseActive()`, `forceRelease(sale)`, `getActiveLockId()`. No React, no DOM; the API functions are injected. The page wires it to the real `salesApi` functions and to `setSelectedSaleForCheckout`/`setSelectedSaleForEdit`/`setError`.
2. New `holdOrderLockController.test.js` with injected spies (no `fetch`) covering: claim action `Checkout`/`Editing`; guard skips `claimSale` when locked by another; 409 failure triggers `reload` + propagates the message; `releaseActive` calls `releaseSale(id)` and clears; `forceRelease` calls `releaseSale(id, true)` and reloads.
3. New `renderToString` test(s) for `PendingOrderLockBadge` / `PendingOrderDesktopRow` / `PendingOrderMobileCard` asserting: badge text for lock-by-other vs lock-by-mine vs free; Cobrar/Editar `disabled` when locked by other; "Liberar" present only when `isElevated && isLockedByOther`, absent otherwise.
4. Keep `salesApi.claim.test.js` and `holdLock.test.js` as-is.

If the user explicitly requires true click-level coverage, that should be a **separate change** that stands up a jsdom harness once and reuses it (also unblocks 8.120 E2 / `CartContext`), rather than inflating this change's dependency and flakiness surface.

### Risks

- The extraction touches production code (`PendingOrdersPage.jsx`); behavior drift is the main risk, mitigated by keeping the existing error/reload semantics (`handleStartCheckout` `:137-140`, `releaseActiveLock` `:119-128`) byte-for-behavior.
- Test-only 400-line budget: this change is well under the guard, but the jsdom option (1) is not.
- `renderToString` presentational tests must build sale fixtures with the fields the components read (`date`, `customer*`, `items`, `payments`, `totalBsS`, `totalUSD`) — invalid fixtures throw during render.
- No money/history impact: nothing here touches `AppliedRate`/`TotalBsS` snapshots or `decimal` rules (`rules.md` not implicated); no EF migration.
- AGENTS.md rule 1 (no comments) and test naming `Metodo_Escenario_ResultadoEsperado` (`docs/coding-guidelines.md` §6.1) must be honored; web suite currently 156/156 (`docs/reporte.txt:8044`).

### Ready for Proposal

Yes. The orchestrator can proceed to proposal/spec/design without a per-phase confirmation. Proposal should name the in-scope project `Web.Frontend` with its project-local command `npm test` (and `npm run lint`), confirm zero new dependencies, and record the open question below.

**Open question for the proposal:** Does the user accept "pure lifecycle helper + `renderToString` render tests" as satisfying 8.121 E1, or is a click-level jsdom harness mandatory? Recommendation: accept option 3 now and scope jsdom as its own change.
