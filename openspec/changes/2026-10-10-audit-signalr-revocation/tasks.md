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

- [x] 1.1 `ExchangeRateHub.UserGroup` + `OnConnectedAsync` con grupo `user:{id}`.
- [x] 1.2 `SecurityStampValidator` con IHubContexts opcionales; `DisconnectUserSessionsAsync` (ambos hubs, evento exacto, fail-soft); `RevokeUserStampAsync` empuja; `InvalidateUserSessionsAsync` en la interfaz.
- [x] 1.3 Caminos: ChangePassword + UpdateUser/SoftDelete/Reactivate/HardDelete (writer) y ResetTemporaryPassword (padre, misma clase; spec enmendada); Phase3 L189/L262 ajustados al contrato nuevo (padre, reportado).
- [x] 1.4 `StampValidationHubFilter` (connect+invoke, forceImmediate, Abort+HubException con mensajes exactos) registrado en `AddSignalR`.
- [x] 1.5 15 tests nuevos (13 unit + 2 integración opt-in); RED compile-level → GREEN; focused 26/26; suite 2195/2195; commit `53731df`.
- [x] 1.6 Build 0/0; commit `53731df`.

## T2 (S3 WPF) — Clientes WPF manejan ForceDisconnect

- [x] 2.1 Ambos servicios: `ForceDisconnect` → log + StopAsync + `ForceDisconnected` una vez (guard, post-dispose suprimido); seams internal + InternalsVisibleTo (precedente 8.75).
- [x] 2.2 12 tests nuevos; focused 54/54; suite 2207/2207; commit `2bac982`.
- [x] 2.3 Build 0/0.

## T3 (S3 Web + bundle) — Clientes Web manejan ForceDisconnect

- [x] 3.1 `signalr.js` (off/on, stop, libera módulo, `pos_unauthorized`) y `authorizationHub.js` (handler por conexión, sin duplicados); `connectionFactory` aditivo.
- [x] 3.2 6 tests web nuevos (RED 3F → GREEN); npm test 379/379; lint 0.
- [x] 3.3 Bundle regenerado (11 rehashed + index.html) y verificado (strings presentes); commit `2c79b5c`.

## T4 (S1-S3) — Verificación + cierre

- [x] 4.1 Verificador independiente: 6/6 COMPLIANT; mensajes byte a byte; fail-soft real; ajustes auditados; F1/F2 registrados; F3-F5 INFOs.
- [x] 4.2 Suite CON Postgres 2207/2207 + cobertura 0.8886/0.9057/0.8621 (exit 0) + build 0/0 + web 379/379 + lint 0.
- [x] 4.3 `verify-report.md` + ANEXO 8.157 + cierre del tracker + commit `docs(8.157)`.

## Notes

- Tracker: `odd/tasks/auditoria-signalr-revocacion.md`.
- F2 (G1): binding WPF sin test runtime; cerrarlo requiere factory inyectable o E2E.
- Los writers NO commitean; el padre commitea por unidad (T3 incluyó el bundle).
