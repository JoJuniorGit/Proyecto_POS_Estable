# Spec (delta): Identidad y tasa del adelanto de efectivo — SEC-03/SEC-04 (8.154)

Fuente: auditoría integral re-emitida. SEC-03: "Eliminar `request.UserName` y `request.CashierId` del contrato `CashAdvanceRequest`; obtener obligatoriamente la identidad del token JWT". SEC-04: "Resolver la tasa anclada en el backend antes de invocar al coordinador".

## ADDED Requirements

### Requirement: REQ-CAI-01 — La identidad del adelanto sale solo del token

`CashDrawerController.ProcessCashAdvanceAsync` MUST resolver la identidad así:

1. `if (_currentUserService.UserId == null || !int.TryParse(_currentUserService.UserId, out int authUserId)) return this.ApiUnauthorized("Sesión inválida.");` (401).
2. `var user = await _userService.GetUserAsync(authUserId, cancellationToken) ?? throw new UnauthorizedAccessException("Usuario no encontrado.");` (→403 por middleware).
3. `int cashierId = user.Id; string userName = user.Name;` y pasar esos valores al coordinador.

`CashAdvanceRequest` MUST eliminar `CashierId` y `UserName` (los clientes que aún los envíen reciben 200: STJ ignora miembros desconocidos, pero los valores no se usan). El cliente WPF MUST dejar de enviarlos.

#### Scenario: Body suplantador es ignorado

- GIVEN token del cajero A (GetUserAsync(A) → Name "Cajero A")
- WHEN `POST /api/cashdrawer/cash-advance` con body que incluye `userName: "Administrador General"` y `cashierId: 99`
- THEN el coordinador recibe `cashierId = A.Id` y `userName = "Cajero A"`; ninguna transacción usa los valores del body.

#### Scenario: Sin sesión válida

- GIVEN `UserId` nulo o no numérico
- WHEN el POST
- THEN 401 `"Sesión inválida."`.

#### Scenario: Usuario inexistente

- GIVEN token con `UserId` de un usuario no persistido
- WHEN el POST
- THEN `UnauthorizedAccessException("Usuario no encontrado.")` → 403 (middleware).

#### Scenario: El cliente WPF no envía identidad

- GIVEN `CashDrawerService.ProcessCashAdvanceAsync`
- WHEN se serializa la request
- THEN el body no contiene `CashierId` ni `UserName`.

### Requirement: REQ-CAI-02 — La tasa del adelanto se ancla antes del coordinador

`ProcessCashAdvanceAsync` MUST resolver `decimal anchoredRate = await ResolveAnchoredRateAsync(request.ExchangeRate, referenceId: request.SessionId, cancellationToken);` y pasar `anchoredRate` al coordinador. Semántica heredada (idéntica a `AddTransaction`): ≤ tolerancia (`RateDeviationTolerancePct`, default 0.10) → tasa cliente redondeada; > tolerancia → tasa BCV del día; ≥ ±100% → `ArgumentException` con el mensaje exacto "La tasa de cambio {rate} fue rechazada: excede ±100% de la tasa BCV oficial ({official}). Contacte al supervisor."; sin BCV → fail-open.

#### Scenario: Desvío fuera de tolerancia ancla

- GIVEN BCV 60 y tasa cliente 67 (11.7%)
- WHEN el adelanto
- THEN la tasa usada por el coordinador (comisión/movimientos) es 60.

#### Scenario: Rechazo ≥ ±100%

- GIVEN BCV 60 y tasa cliente 120
- WHEN el adelanto
- THEN `ArgumentException` con "excede ±100%" y sin efectos persistidos.

#### Scenario: Fail-open sin BCV

- GIVEN sin tasa del día
- WHEN el adelanto con tasa 80.463
- THEN se usa 80.47 (redondeo ceiling cliente).

### Requirement: REQ-CAI-03 — Cobertura de pruebas

- Tests de identidad (nuevo archivo autorizado): token manda sobre body, 401 sin sesión, 403 usuario inexistente.
- Tests de anclaje del adelanto extendiendo el arnés A5 de `CashDrawerRateAnchorTests` (captura de la tasa recibida por el coordinador o su efecto observable).
- Ajustes de tests existentes (p. ej. `CancellationPropagationTests`: UserId + GetUserAsync mockeados y `CashAdvanceRequest` sin los campos eliminados) reportados nombradamente.
