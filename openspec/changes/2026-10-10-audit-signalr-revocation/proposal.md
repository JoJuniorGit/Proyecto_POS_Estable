# Proposal: Revocación de sesiones SignalR (SEC-07) — ANEXO 8.157

## Contexto

Tramo 5 autorizado ("haz 8.157"). Alcance: **SEC-07** — conexiones WebSocket zombis tras revocación de credenciales. La autenticación de SignalR ocurre solo en el handshake; al revocar un usuario (rotación de sello o desactivación) el socket queda abierto y sigue recibiendo eventos, y no hay validación de sello por invocación.

Verificación previa (estática, de este plan):

| Aspecto | Estado | Evidencia |
|---|---|---|
| Hubs | `AuthorizationHub` agrupa por `user:{id}` (+`role:elevated`); `ExchangeRateHub` vacío SIN grupos (solo `[Authorize]`) | `AuthorizationHub.cs:29-44`, `ExchangeRateHub.cs` |
| Revocación | 6 caminos: Logout (`RevokeUserStampAsync`: rota+sella caché); ChangePassword / UpdateUser(credenciales) / SoftDelete / Reactivate / HardDelete **solo `InvalidateUserStamp` (caché)** | `AuthController.cs:101,144`, `UsersController.cs:97,125,148,167` |
| Push de desconexión | INEXISTENTE (sin `IHubContext` en el validador) | grep 0 |
| IHubFilter | INEXISTENTE | grep 0 |
| Validación por request | `SecurityStampValidationMiddleware` compara `security_stamp` (claim JWT) vs BD con micro-caché (45 s / 5 s estricto) — el patrón a espejar | `SecurityStampValidationMiddleware.cs:42-66`; `TokenService.cs:76` |
| Registro | `AddSignalR()` en `ServiceCollectionExtensions.cs:112`; `MapHub` ambas rutas `PipelineExtensions.cs:89-90` | — |
| Clientes | WPF `AuthorizationHubService`/`ExchangeRateService` y Web `authorizationHub.js`/`signalr.js` con `WithAutomaticReconnect`; sin manejo de `ForceDisconnect` | grep 0 del evento |

## Alcance

1. **S1 (push de desconexión)**: `RevokeUserStampAsync` rota + invalida + **envía `ForceDisconnect` a todas las conexiones del usuario** en ambos hubs (grupos `user:{id}`; `ExchangeRateHub` gana grupo en `OnConnectedAsync`). Nuevo `InvalidateUserSessionsAsync(userId)` (caché + push) invocado por los 5 caminos que hoy solo limpian caché. Fail-soft (el push nunca rompe la revocación).
2. **S2 (IHubFilter)**: `StampValidationHubFilter : IHubFilter` global que valida el sello en `OnConnectedAsync` y en cada `InvokeMethodAsync` con chequeo inmediato (ventana estricta 5 s); inválido/faltante → `Context.Abort()` + `HubException` con los mensajes EXACTOS del middleware. Cubre reconexiones automáticas de tokens revocados.
3. **S3 (clientes)**: WPF (ambos servicios) y Web (ambos servicios) manejan `ForceDisconnect`: log, `StopAsync`/`stop` (sin auto-reconnect) y notificación; se reutiliza el mecanismo de sesión expirada existente si lo hay.

Fuera de alcance: PERF-03/04/05, CLEAN-01/03/04 y el resto del roadmap.

## Enfoque y decisiones

- D1: los grupos de `ExchangeRateHub` se agregan en `OnConnectedAsync` (espejo de `AuthorizationHub`), así el push usa grupos en ambos y queda simétrico con el snippet de la auditoría.
- D2: el push vive en `SecurityStampValidator` (audit) con `IHubContext<AuthorizationHub>?`/`IHubContext<ExchangeRateHub>?` **opcionales** → las construcciones directas en tests (`new SecurityStampValidator(db, cache)`) siguen compilando y el push es no-op sin hubs.
- D3: evento exacto `"ForceDisconnect"` sin payload (snippet de la auditoría); los clientes muestran texto local.
- D4: fail-soft en el push (AppLogger.LogWarn) — la revocación de credenciales es una operación de seguridad que no debe fallar por el transporte en tiempo real.
- D5: el filtro usa `forceImmediateCheck: true` (la ventana estricta de 5 s acota el costo; latencia de revocación ≤5 s para invocaciones y 0 para conexión nueva).

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~600-800 líneas (servidor + 2 clientes + bundle web).
- Riesgo principal: interacción con el auto-reconnect de los clientes (mitigado: `stop` explícito en el handler + filtro que aborta reconexiones con token revocado). El harness de integración de hubs (8.150) permite ejercitar el filtro con `Mock<HubCallerContext>` y TestServer.
- RDD: off (clone-local) → verificación independiente + suite/cobertura.

## Tramos siguientes

- 8.158: SRE-03 + CLEAN-03. 8.159: CLEAN-01/04 + PERF-03.
