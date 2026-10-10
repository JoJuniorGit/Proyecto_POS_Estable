# Revocación de sesiones SignalR (SEC-07)

Objetivo: tramo 8.157 del roadmap de auditoría — desconexión forzosa de sockets SignalR al revocar credenciales (push a ambos hubs en los 6 caminos de revocación) + `IHubFilter` global que valida el sello por conexión e invocación + clientes WPF/Web que cierran sus hubs al recibir `ForceDisconnect`. Change: `openspec/changes/2026-10-10-audit-signalr-revocation/` (ANEXO 8.157). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · web `npm test` + `npm run lint` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (push de desconexión)**: `ExchangeRateHub` con grupo `user:{id}`; `SecurityStampValidator` con IHubContexts opcionales + `DisconnectUserSessionsAsync` (evento exacto `"ForceDisconnect"` sin payload, fail-soft); `RevokeUserStampAsync` y los 5 caminos de `InvalidateUserStamp` (ChangePassword/UpdateUser/SoftDelete/Reactivate/HardDelete) empujan vía `InvalidateUserSessionsAsync`. Detalle: `specs/hub-revocation/spec.md`.
- **S2 (IHubFilter)**: `StampValidationHubFilter` registrado en `AddSignalR`; valida `security_stamp` en `OnConnectedAsync` e `InvokeMethodAsync` (forceImmediate true); abort + `HubException` con los mensajes exactos del middleware. Detalle: mismo spec.
- **S3 (clientes)**: WPF (`AuthorizationHubService`/`ExchangeRateService`) y Web (`signalr.js`/`authorizationHub.js`): `ForceDisconnect` → log + stop (sin auto-reconnect) + evento/notificación; bundle web regenerado. Detalle: `specs/hub-clients-force-disconnect/spec.md`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S2 | delegated (writer) | Servidor: push + filtro + caminos + tests | hecho — RED compile-level→GREEN; focused 26/26; suite 2195/2195; ajustes del padre (Phase3 + ResetTemporaryPassword); commit 53731df |
| T2 | S3 | delegated (writer) | WPF: ForceDisconnect en ambos hubs + tests | hecho — 12 tests; focused 54/54; suite 2207/2207; seams internal; commit 2bac982 |
| T3 | S3 | delegated (writer) | Web: ForceDisconnect + tests + bundle | hecho — RED 3F→GREEN; web 379/379; lint 0; bundle 11 rehashed+index.html; commit 2c79b5c |
| T4 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.157 + cierre | hecho — PASS WITH WARNINGS (6/6 COMPLIANT; F1 trazabilidad, F2 binding WPF divulgado, F3-F5 INFOs); cobertura 0.8886/0.9057/0.8621 exit 0; ANEXO 8.157; commit de cierre en L6 |

## Log

- L1 (2026-10-10) — Pedido del usuario, verbatim: "haz 8.157".
- L2 (2026-10-10) — Verificación previa: push INEXISTENTE; IHubFilter INEXISTENTE; `ExchangeRateHub` sin grupos; 6 caminos de revocación mapeados (Logout rota sello; ChangePassword/UpdateUser/SoftDelete/Reactivate/HardDelete solo invalidan caché); middleware de sellos (mensajes exactos a espejar) y claim `security_stamp` en `TokenService:76`; registro `AddSignalR` en `ServiceCollectionExtensions:112`; clientes con `WithAutomaticReconnect` y sin manejo del evento; harness de integración de hubs (8.150) mockea `HubCallerContext` → filtro unit-testeable.
- L3 (2026-10-10) — Próximo: T1 delegado a writer (superficies: ExchangeRateHub, StampValidationHubFilter nuevo, SecurityStampValidator, ISecurityStampValidator, ServiceCollectionExtensions, UsersController, AuthController + tests nuevos/extensión de integración).
- L4 (2026-10-10) — T1 (servidor) completado: push `ForceDisconnect` fail-soft a ambos hubs (ExchangeRateHub gana grupo `user:{id}`), `InvalidateUserSessionsAsync` + 6 caminos (writer migró los 5 + el padre amplió a `ResetTemporaryPassword` — misma clase, regenera sello — con enmienda del spec), `StampValidationHubFilter` global registrado en AddSignalR con mensajes exactos del middleware. Writer con RED compile-level → GREEN focused 26/26; ajustes del padre por contrato viejo: `Phase3AuthenticationAndPolicyTests` L189/L262 → `InvalidateUserSessionsAsync` (reportados). Extensión del harness de integración con 2 tests (reconexión rechazada por el filtro; invoke revocado aborta). Suite completa 2195/2195; build 0/0. Commit en el hash del mensaje `fix(8.157) ... T1`.
- L5 (2026-10-10) — T2 (2bac982) y T3 (2c79b5c) completados: WPF implementó el manejo en ambos servicios (stop + evento una vez, seams internal con InternalsVisibleTo; 12 tests; focused 54/54; suite 2207/2207) y Web implementó `handleForceDisconnect` + handler del hub de autorizaciones reutilizando `pos_unauthorized` (api.js/AuthContext), con `connectionFactory` inyectable aditivo y 6 tests (RED 3F→GREEN; web 379/379; lint 0); bundle regenerado (11 assets rehashed + index.html, strings verificados).
- L6 (2026-10-10) — Cierre: verificación independiente PASS WITH WARNINGS (6/6 requisitos COMPLIANT; mensajes byte a byte; fail-soft real; DI del filtro comprobada; F1 trazabilidad del commit T3 y F2 binding WPF sin test runtime divulgado; F3-F5 INFOs) + verify-report.md + ANEXO 8.157 + state/tasks actualizados + commit de cierre (hash en git log). Suite CON Postgres 2207/2207; cobertura 0.8886/0.9057/0.8621 (exit 0). Entrega pendiente: push/PR/merge (decisión del mantenedor; cadena stacked-to-main cacheada). Próximo tramo: 8.158 (SRE-03 + CLEAN-03) cuando se autorice.
