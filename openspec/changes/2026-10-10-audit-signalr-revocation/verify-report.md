# Verify Report: Revocación de sesiones SignalR (8.157)

## Veredicto

**PASS WITH WARNINGS** — 6/6 requisitos COMPLIANT (REQ-HREV-01/02/03, REQ-HFC-01/02/03). Sin CRITICAL. Warnings: F1 (trazabilidad: el commit de T3 se creó durante la verificación) y F2 (el binding WPF del handler no es observable en el harness unit — `enableRealtime:false`; limitación divulgada desde T2). INFOs F3–F5 documentados.

## Gates de cierre

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias, 0 errores**.
- Suite completa **CON PostgreSQL real**: **2207/2207**, 0 fallas, 0 omitidas (2180 base + 15 T1 + 12 T2).
- Cobertura CON Postgres (coverage.runsettings + `check-coverage.py`, exit 0): Core **0.8886** / Sales.Module **0.9057** / Inventory.Module **0.8621**.
- Web: `npm test` **379/379**; `npm run lint` exit 0. Bundle regenerado y commiteado (11 assets rehashed + `index.html`; `"ForceDisconnect"` y `pos_unauthorized` presentes en el chunk de ExchangeRateContext; `posSessionRevoked` inexistente).
- `git status` limpio en HEAD `2c79b5c`.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-HREV-01 | COMPLIANT | `ExchangeRateHub` con `UserGroup`/`OnConnectedAsync` (formato verificado por test); `DisconnectUserSessionsAsync` fail-soft por hub (test real con un hub que lanza y el otro recibe); `RevokeUserStampAsync` rota+invalida+empuja; **6 caminos** con `InvalidateUserSessionsAsync` (incl. ResetTemporaryPassword, ampliación del padre con spec espejada); cero call-sites productivos del viejo `InvalidateUserStamp`; args del evento vacío verificado. |
| REQ-HREV-02 | COMPLIANT | Filtro con validación en connect/invoke, `forceImmediateCheck: true` (verify del tercer argumento), `Abort()` antes del throw, mensajes **byte a byte** iguales al middleware (comparación programática), `OnDisconnectedAsync` delega; registro `AddFilter<T>` con DI comprobado (scope por conexión vía `HubFilterFactory`); 8 unit + 2 integración opt-in (reconexión rechazada con mensaje canónico; invoke revocado aborta). |
| REQ-HREV-03 | COMPLIANT | 15 tests servidor (13 unit + 2 integración); harness existente intacto (cambios aditivos). |
| REQ-HFC-01 | COMPLIANT (F2) | Ambos servicios WPF: const exacta, log, stop explícito (corta auto-reconnect), evento una vez con guard, post-dispose suprimido; `internal` + `InternalsVisibleTo` (precedente 8.75); 12 tests (stop/skip/fail-soft/idempotencia/post-dispose). |
| REQ-HFC-02 | COMPLIANT | Web: `handleForceDisconnect` (off/on, stop, libera módulo, notifica vía `pos_unauthorized` — mecanismo real: `api.js` lo emite y `AuthContext` lo escucha); `authorizationHub.js` idem (sin duplicar en doble connect); `connectionFactory` inyectable aditivo. |
| REQ-HFC-03 | COMPLIANT | 4 tests nuevos en `signalr.test.js` (stop exacto, no-reconnect, no duplicación, release+rebuild, evento correcto) + 2 en `authorizationHub.test.js`; npm test/lint verdes; bundle verificado (31/31 refs de index.html existen). |

## Hallazgos (honestos)

- **F1 (WARNING, trazabilidad)**: el commit de T3 (`2c79b5c`) se creó durante la verificación (el padre commitea por unidad); el estado final es consistente y limpio. No es defecto de código.
- **F2 (WARNING, test gap acotado)**: la línea de binding `On(ForceDisconnectEvent, …)` de los servicios WPF no se ejecuta en los unit tests (`AuthorizationHubServiceTests` usa `enableRealtime:false`; el arnés no puede fakeear `HubConnection` — ctor no accesible). La lógica del stop/evento SÍ está cubierta vía seams internos. Limitación divulgada en T2; cerrarla requeriría un harness de transporte SignalR fake o E2E.
- **F3 (INFO)**: asimetría de anclaje del nombre del evento entre los dos archivos de tests WPF (uno por reflexión a la const del servidor, otro por literal).
- **F4 (INFO)**: `_forceDisconnectRaised` es one-shot por instancia (por diseño/spec).
- **F5 (INFO)**: el tracker registraba hasta L4 en el momento de la verificación; L5/L6 se agregaron en el cierre.

## Auditoría de tests (sin enmascaramiento)

- Ajustes de `Phase3AuthenticationAndPolicyTests` L189/L262: solo la línea de `Verify` cambió al contrato nuevo; el resto de aserciones intactas.
- Fail-soft verificado en ambas direcciones (no excepción + push al hub sano + rotación + validación posterior).
- Filtro: asserts de mensaje exacto, `Times.Once/Never` de `Abort` y `next`, y verify del `forceImmediateCheck`.
- Harness de integración: opt-in por default; `CreateConnection` refactor aditivo; ningún test preexistente modificado.
- RED no re-observado en modo read-only (GREEN observado en todos los filtros y suites).

## Limitaciones

- F2 (binding WPF sin test runtime); E2E web (`test:e2e`) no corrido; bundle verificado estáticamente (strings/hashes/integridad); REDs reportados por los writers no reproducibles read-only.

## Artefactos / commits

Change `2026-10-10-audit-signalr-revocation`; tracker `odd/tasks/auditoria-signalr-revocacion.md`; ANEXO 8.157. Commits: `91dbca9` (plan) + `53731df` (T1) + `2bac982` (T2) + `2c79b5c` (T3+bundle) + commit de cierre.
