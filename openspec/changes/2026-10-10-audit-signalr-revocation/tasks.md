# Tasks: Revocación de sesiones SignalR (8.157)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~600-800 |
| Budget risk | High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 3 unidades + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. Servidor (T1) | `--filter "FullyQualifiedName~SecurityStampRevocation\|~StampValidationHubFilter\|~AuthorizationHubIntegration"` | Revertir hubs/validador/filtro/controllers + tests |
| 2. WPF (T2) | `--filter "FullyQualifiedName~AuthorizationHubService\|~ExchangeRateService"` | Revertir ambos servicios + tests |
| 3. Web + bundle (T3) | `npm test` (Web.Frontend) + regenerar bundle | Revertir web + bundle |
| 4. Verificación + cierre (T4) | suite completa (con Postgres) + cobertura | Revertir docs |

## T1 (S1+S2) — Servidor: push de desconexión + IHubFilter

- [ ] 1.1 `ExchangeRateHub.UserGroup(userId)` + `OnConnectedAsync` que agrega al grupo `user:{id}` (espejo de AuthorizationHub).
- [ ] 1.2 `SecurityStampValidator`: ctor con `IHubContext<AuthorizationHub>?`/`IHubContext<ExchangeRateHub>?` opcionales; `DisconnectUserSessionsAsync` (grupos de ambos hubs, evento exacto `"ForceDisconnect"`, fail-soft); `RevokeUserStampAsync` lo invoca; nuevo `InvalidateUserSessionsAsync` (caché + push) en `ISecurityStampValidator`.
- [ ] 1.3 Caminos de revocación: ChangePassword (`AuthController:144`), UpdateUser/SoftDelete/Reactivate/HardDelete (`UsersController`) pasan a `InvalidateUserSessionsAsync` (cuando el validador esté presente).
- [ ] 1.4 `StampValidationHubFilter` (nuevo): valida en `OnConnectedAsync` e `InvokeMethodAsync` con `forceImmediateCheck: true`; inválido/faltante → `Context.Abort()` + `HubException` con los mensajes EXACTOS del middleware; registrado en `AddSignalR(options => options.AddFilter<...>())`.
- [ ] 1.5 Tests (RED→GREEN): validador con IHubContexts mockeados (grupo `user:{id}` en ambos hubs, evento, fail-soft, no-op sin hubs); filtro (conectar/invocar válidos, sello inválido, claims faltantes) con `Mock<HubCallerContext>`; extensión de `AuthorizationHubIntegrationTests` si aporta (reconexión con token revocado rechazada).
- [ ] 1.6 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.157)` (lo hace el padre).

## T2 (S3 WPF) — Clientes WPF manejan ForceDisconnect

- [ ] 2.1 `AuthorizationHubService` y `ExchangeRateService`: `On("ForceDisconnect")` → log (`ClientStateLogger.LogWarning`), `StopAsync` (sin auto-reconnect) y evento `ForceDisconnected`; idempotente; dispose intacto.
- [ ] 2.2 Tests extendidos (patrón existente del harness de ambos servicios, reportar cómo se faked el hub si aplica).
- [ ] 2.3 Build 0/0 + focused + suite; commit `fix(8.157)` (lo hace el padre).

## T3 (S3 Web + bundle) — Clientes Web manejan ForceDisconnect

- [ ] 3.1 `signalr.js` y `authorizationHub.js`: `.on('ForceDisconnect')` → `stop()` + notificación (reutilizar el mecanismo de sesión expirada existente; documentar la elección). El auto-reconnect no debe reactivar el socket tras el stop.
- [ ] 3.2 Tests web (`npm test`) extendidos; `npm run lint` 0.
- [ ] 3.3 Bundle `Backend.API/wwwroot` regenerado (`npm run build`) y commiteado junto al cambio.
- [ ] 3.4 Commit `fix(8.157)` (lo hace el padre).

## T4 (S1-S3) — Verificación + cierre

- [ ] 4.1 Verificador independiente: push a ambos hubs en los 6 caminos, filtro (connect/invoke), clientes (stop sin reconexión), bundle consistente.
- [ ] 4.2 Suite completa (con Postgres) + cobertura + build 0/0 + npm test/lint.
- [ ] 4.3 `verify-report.md` + ANEXO 8.157 + cierre del tracker + commit `docs(8.157)`.

## Notes

- Tracker: `odd/tasks/auditoria-signalr-revocacion.md`.
- Los writers NO commitean; el padre commitea por unidad (T3 incluye el bundle regenerado).
