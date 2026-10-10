# Spec (delta): Clientes SignalR manejan ForceDisconnect (SEC-07) (8.157)

Fuente: auditoría re-emitida, SEC-07 — la sesión WebSocket del usuario revocado debe cerrarse de verdad (y no reabrirse por auto-reconnect).

## ADDED Requirements

### Requirement: REQ-HFC-01 — WPF cierra sus hubs al recibir ForceDisconnect

`AuthorizationHubService` y `ExchangeRateService` MUST registrar `On("ForceDisconnect")` que: registra el evento (`ClientStateLogger.LogWarning`, con nombre del servicio), detiene la conexión (`StopAsync`; el auto-reconnect NO debe reactivarla) y eleva un evento `ForceDisconnected` (idempotente; `Dispose` intacto).

#### Scenario: Hub detenido

- GIVEN un servicio con conexión activa
- WHEN llega `ForceDisconnect`
- THEN la conexión se detiene, se loguea y `ForceDisconnected` se eleva una vez.

#### Scenario: Sin reconexión tras el cierre

- GIVEN el handler ejecutado
- THEN el auto-reconnect no vuelve a levantar el socket (stop explícito).

### Requirement: REQ-HFC-02 — Web detiene sus hubs al recibir ForceDisconnect

`signalr.js` (exchange-rate) y `authorizationHub.js` MUST registrar `.on('ForceDisconnect')` que llama `connection.stop()` y notifica; MUST reutilizar el mecanismo existente de sesión expirada si lo hay (documentar la elección; si no, dispatch de un evento de sesión revocada y documentar que el manejo de 401 existente completa el flujo). El auto-reconnect MUST NOT reactivar el socket tras el stop (patrón `.off/.on` existente de rewire respetado).

#### Scenario: Hub web detenido

- GIVEN el hub web conectado
- WHEN llega `ForceDisconnect`
- THEN `stop()` se invoca una vez y no se reconecta.

### Requirement: REQ-HFC-03 — Cobertura cliente

- WPF: extender `AuthorizationHubServiceTests` (y el harness equivalente de `ExchangeRateService` si existe) con el escenario de `ForceDisconnect` (patrón existente del harness; reportar cómo se faked el hub).
- Web: extender `authorizationHub.test.js` (fake builder existente) y agregar/ajustar el test de `signalr.js` si el arnés lo permite.
- `npm test` + `npm run lint` verdes; bundle `Backend.API/wwwroot` regenerado con el web change.
