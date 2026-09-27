# Apply Progress: enforce-hold-claim-precondition

Mode: standard (strict_tdd false)

## Slice 1 (backend) - complete
- `Sales.Module/Exceptions/HoldNotClaimedException.cs` creada; `EnsureHoldClaimAccess` exige claim activo del actor (actor nulo rechazado; claim ajeno sigue `SaleLockedException`).
- Migracion de los 27 casos + `HoldNotClaimedPreconditionTests.cs` (14 casos nuevos).
- Evidencia: `dotnet build CommandCenter.slnx -c Release` 0/0; `dotnet test` 985/985 (2026-09-14).

## Slice 2 (web Anular) - complete
- `PendingOrdersPage.handleConfirmCancelSale` reclama `Editing` antes de `cancelSale`; si el claim falla no anula (mensaje + reload via controlador); `releaseActive` best-effort en `finally`.
- Evidencia: `npm test` 178/178; `npm run lint` 0 (2026-09-14; re-ejecutado tras interrupcion del agente).

## Slice 3 (web carrito) - complete
- Tareas 3.3-3.4: rechazar OnHold en `CartContext` (restore/persist/cache/rate effect/loadExistingSale) con mensaje hacia Cuentas Abiertas.
- `CartContext.jsx`: cache init (:30) solo acepta `Pending`; persist (:65) limpia claves `active_pos_*` si OnHold; `restoreOrStartSale` (:196) rechaza OnHold, limpia sesión, crea venta nueva; `loadExistingSale` (:166) rechaza OnHold; rate effect (:240) solo aplica a `Pending`. `createNewSale` limpia error al crear venta nueva para que el mensaje no persista.
- Evidencia: `npm test` 178/178; `npm run lint` 0 (2026-09-14).
- Líneas cambiadas: +24 (536→560), dentro del presupuesto.

## Verificacion final - pending
- 3.5-3.7 y `sdd-verify` (acquire + settle) antes de archive.
