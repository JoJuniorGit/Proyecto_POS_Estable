# Spec (delta): Revocación de sesiones SignalR — servidor (SEC-07) (8.157)

Fuente: auditoría integral re-emitida, SEC-07: "1. Inyectar `IHubContext<AuthorizationHub>` e `IHubContext<ExchangeRateHub>` en `SecurityStampValidator`: `await _authHubContext.Clients.Group(AuthorizationHub.UserGroup(userId)).SendAsync("ForceDisconnect");` 2. Implementar un `IHubFilter` global que invoque `ValidateStampAsync` en cada llamada a método del Hub."

## ADDED Requirements

### Requirement: REQ-HREV-01 — La revocación cierra los sockets del usuario

- `ExchangeRateHub` MUST exponer `UserGroup(userId)` y agregar cada conexión a `user:{id}` en `OnConnectedAsync` (espejo de `AuthorizationHub`).
- `ISecurityStampValidator` MUST exponer `Task InvalidateUserSessionsAsync(int userId)` = invalidar caché + push de desconexión.
- `RevokeUserStampAsync` MUST rotar el sello, invalidar la caché y enviar `"ForceDisconnect"` (sin payload) a `Clients.Group(user:{id})` de **ambos** hubs vía `IHubContext<AuthorizationHub>?`/`IHubContext<ExchangeRateHub>?` (opcionales; ausentes ⇒ no-op).
- Los 6 caminos que hoy solo invalidan caché (`ChangePassword`, `ResetTemporaryPassword` — ampliación post-T1: regenera el sello, misma clase —, `UpdateUser` con cambio de credenciales/rol, `SoftDelete`, `Reactivate`, `HardDelete`) MUST llamar `InvalidateUserSessionsAsync`.
- El push MUST ser fail-soft: cualquier error se registra (`AppLogger.LogWarn`) y NO interrumpe la revocación.

#### Scenario: Logout/revocación desconecta

- GIVEN un usuario con conexiones en ambos hubs
- WHEN `RevokeUserStampAsync`/`InvalidateUserSessionsAsync`
- THEN ambos grupos `user:{id}` reciben `"ForceDisconnect"` y el sello quedó rotado/invalidado.

#### Scenario: Sin hubs (tests unitarios del validador)

- GIVEN `SecurityStampValidator(db, cache)` sin IHubContexts
- WHEN se revoca
- THEN no lanza y la revocación queda completa.

### Requirement: REQ-HREV-02 — El IHubFilter valida el sello por conexión e invocación

`StampValidationHubFilter : IHubFilter` MUST registrarse en `AddSignalR(options => options.AddFilter<StampValidationHubFilter>())` y:

- En `OnConnectedAsync` y `InvokeMethodAsync` MUST parsear `NameIdentifier|sub` + `security_stamp` del `Context.User` y llamar `ValidateStampAsync(userId, stamp, forceImmediateCheck: true)`.
- Sello faltante → `Context.Abort()` + `HubException("El token no posee un sello de seguridad válido. Por favor inicie sesión nuevamente.")`.
- Sello inválido → `Context.Abort()` + `HubException("La sesión ha sido revocada o las credenciales del usuario cambiaron. Inicie sesión nuevamente.")`.
- Válido → continúa el pipeline (`next`).

#### Scenario: Reconexión con token revocado

- GIVEN un token cuyo sello ya no coincide con la BD
- WHEN el cliente (auto-reconnect) intenta conectar o invocar
- THEN la conexión se aborta con la `HubException` canónica.

#### Scenario: Conexión/invocación válida

- GIVEN sello vigente
- THEN `OnConnectedAsync`/`InvokeMethodAsync` fluyen sin cambios de comportamiento.

### Requirement: REQ-HREV-03 — Cobertura servidor

- Tests del validador con `IHubContext` mockeados (grupo y evento por hub; fail-soft; no-op sin hubs; rotación intacta).
- Tests del filtro con `Mock<HubCallerContext>`: válidos (connect/invoke), sello inválido (abort + mensaje exacto), claims faltantes (abort + mensaje exacto).
- Extensión del harness de integración de hubs (8.150) sólo si aporta cobertura real de reconexión rechazada; documentar.
