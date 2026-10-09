# Spec (delta): Arqueo ciego del efectivo esperado — SEC-06 (8.153)

Fuente: auditoría integral re-emitida, hallazgo SEC-06: "Restringir el acceso estrictamente a supervisores" en `GET /api/cashdrawer/current-balance` (`[Authorize(Roles = "Admin,Manager")]`).

## ADDED Requirements

### Requirement: REQ-BCB-01 — El endpoint de balance solo responde a supervisores

`Backend.API/Controllers/CashDrawerController.GetCurrentBalanceAsync` MUST quedar con `[Authorize(Roles = "Admin,Manager")]` (se retira `Cashier`). Sin otros cambios de comportamiento (mismo route, query, response).

#### Scenario: Cashier rechazado

- GIVEN un token de rol Cashier
- WHEN `GET /api/cashdrawer/current-balance?sessionId=N`
- THEN 403 (sin cuerpo de balance).

#### Scenario: Supervisor habilitado

- GIVEN un token Admin o Manager
- WHEN el mismo GET
- THEN 200 con el balance (comportamiento actual).

### Requirement: REQ-BCB-02 — El rechazo del adelanto no revela el saldo

`CashAdvanceCoordinator.ProcessAsync` MUST rechazar efectivo insuficiente con el mensaje exacto `"Saldo de efectivo en caja insuficiente para el monto solicitado."`, sin incluir el saldo disponible ni el monto requerido (hoy filtra `Disponible: {saldo}` y es accesible al cajero vía 409 del middleware de dominio). El guard contra `availableCash < requested` MUST conservarse.

#### Scenario: Adelanto excesivo

- GIVEN un cajero y un adelanto mayor al efectivo disponible
- WHEN `POST /api/cashdrawer/cash-advance`
- THEN 409 con el mensaje exacto sin cifras; ninguna respuesta contiene el saldo.

### Requirement: REQ-BCB-03 — El cliente WPF oculta el efectivo esperado a no-supervisores

`CashDrawerViewModel` MUST exponer `CanViewTheoreticalBalance` (true solo con rol Admin o Manager; **fail-closed** si no hay sesión). Con `false`: `LoadSessionAsync` MUST NOT invocar `GetCurrentBalanceLocalAsync`, y la representación MUST ser neutra (`FormattedBalanceBsS = "—"`, `FormattedBalanceUsd = string.Empty`) — nunca `0` engañoso. Ingresos/egresos/historial quedan intactos. `ProcessCashAdvanceAsync` MUST NOT invocar el fetch y MUST pasar `null` al diálogo. Con rol supervisor, comportamiento actual sin cambios.

#### Scenario: Cajero abre la vista de Caja

- GIVEN sesión Cashier y sesión de caja activa
- WHEN `LoadSessionAsync`
- THEN `GetCurrentBalanceLocalAsync` no se invoca; `FormattedBalanceBsS == "—"` y `FormattedBalanceUsd == ""`.

#### Scenario: Supervisor abre la vista de Caja

- GIVEN sesión Admin/Manager
- WHEN `LoadSessionAsync`
- THEN el balance se obtiene y formatea como hoy.

#### Scenario: Cajero procesa un adelanto

- GIVEN sesión Cashier
- WHEN `ProcessCashAdvanceAsync`
- THEN el diálogo recibe `null` como efectivo disponible y el fetch no se invoca.

### Requirement: REQ-BCB-04 — El diálogo de adelanto opera sin tope local para no-supervisores

`IDialogService.ShowCashAdvanceRegisterDialogAsync(paymentMethods, decimal? availableCashLocal)`; `CashAdvanceRegisterViewModel.AvailableCashLocal` nullable; `AvailableCashDisplay` = formato N0 con valor, `"—"` si null; el tope local (`CanConfirm`/`ValidateInputs`) MUST aplicarse solo cuando hay valor; con `null` el operador puede confirmar y el rechazo definitivo proviene del servidor (mensaje enmascarado). El XAML del diálogo MUST usar `AvailableCashDisplay`.

#### Scenario: Diálogo para cajero

- GIVEN `availableCashLocal = null`
- WHEN se evalúa un monto cualquiera > 0 con método seleccionado y comisión válida
- THEN el tope local no bloquea (`CanConfirm` true) y `AvailableCashDisplay == "—"`.

#### Scenario: Diálogo para supervisor

- GIVEN `availableCashLocal = 5000`
- WHEN el monto solicitado supera 5000
- THEN `CanConfirm` false y mensaje de tope actual (con cifra) — comportamiento vigente.

### Requirement: REQ-BCB-05 — Cobertura de pruebas

- Backend: authz del endpoint (Cashier 403 / Admin 200 / Manager 200); mensaje del adelanto sin cifras (y los tests existentes por prefijo verdes).
- WPF: cajero ciego (sin fetch, "—", diálogo null); supervisor sin cambios; tests ajustados (sesión Admin en los que asertaban saldo sin sesión; firma nullable en mocks/fakes) reportados nombradamente.
