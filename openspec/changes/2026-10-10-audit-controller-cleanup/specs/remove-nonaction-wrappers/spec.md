# Spec (delta): Erradicación de métodos zombis [NonAction] — CLEAN-01 (8.159)

Fuente: auditoría re-emitida, CLEAN-01: "Erradicar todos los métodos `[NonAction]` de los controladores. Si los tests unitarios requieren invocar los controladores, deben llamar directamente a los métodos canónicos asíncronos pasando `CancellationToken.None`."

## ADDED Requirements

### Requirement: REQ-NAC-01 — Cero wrappers [NonAction]

Los **93** métodos `[NonAction]` passthrough (`public Task<...> X(args) => XAsync(args);`) MUST eliminarse de los 19 archivos de `Backend.API/Controllers` (por grupo):

- **Grupo A (37)**: AuthController(4), UsersController(9), PaymentMethodsController(6), SettingsController(8), ExchangeRateController(4), HealthController(2), ReceiptsController(1), ReservationsController(3).
- **Grupo B (56)**: CashDrawerController(9), DailyClosureController(3), ProductsController(9) + ImportExport(3) + Variants(5), SalesController(6) + Checkout(4) + Claims(2) + Customers(6) + History(3) + HoldOrders(6).

Cero cambios en rutas, atributos, lógica ni firmas de los métodos `*Async`. Tras el cambio, `grep "\[NonAction\]"` en Controllers MUST dar 0.

#### Scenario: Sin wrappers

- GIVEN el árbol final
- THEN no existe ningún `[NonAction]` en controladores y los métodos `*Async` conservan sus firmas.

### Requirement: REQ-NAC-02 — Call sites de tests migrados

Todo call site de tests que invocaba un wrapper MUST pasar al método `*Async` equivalente (mismos argumentos + `CancellationToken.None` o el default), preservando las aserciones existentes (sin debilitarlas ni borrar escenarios). `CancellationPropagationTests.TouchedActionsAndHelpers_DeclareCancellationTokenAsLastParameter` MUST listar los métodos `*Async` canónicos en lugar de los wrappers. Los falsos positivos (reflexión `.GetMethod(`, mocks de servicios con métodos homónimos) no se tocan.

#### Scenario: Suite completa

- GIVEN los grupos migrados
- WHEN corre la suite completa
- THEN 0 fallas, sin tests eliminados (los conteos se mantienen o suben).

#### Scenario: RED de compilación

- GIVEN los wrappers eliminados sin migrar call sites
- THEN la compilación de tests falla nombrando los wrappers ausentes (RED divulgado del refactor).
