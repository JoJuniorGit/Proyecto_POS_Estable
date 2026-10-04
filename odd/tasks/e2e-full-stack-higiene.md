# E2E Full-Stack (Nivel B) + Higiene UIA

Objetivo: que las pruebas E2E de escritorio usen funciones reales del sistema de punta a punta — Backend.API real + PostgreSQL real + cliente WPF sin `--e2e` — con flujos de negocio completos (venta POS y caja/cierre), estabilización de UI Automation y entrada a CI. Branch: V0.15. Entrega: work units (stacked-to-main cacheada). SDD: `openspec/changes/2026-10-04-wpf-e2e-full-stack/`. RDD: off (clone-local). Tests: `dotnet test tests/CommandCenter.Wpf.E2ETests/CommandCenter.Wpf.E2ETests.csproj -c Release` · build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: pedido del usuario (L1) sobre el chequeo del 2026-10-04. Decisiones en L2.

- **S1 (Harness full-stack)**: E2E real = lanzar `Backend.API` contra una base PostgreSQL aislada (`pos_e2e_<suffix>`), esperar `/api/health` = Healthy, sembrar bootstrap por API (admin rotado, rate, producto de prueba), neutralizar de forma segura el settings del cliente (`%LocalAppData%\ProyectoPOS\client_settings.json` con backup/restore) y lanzar `Desktop.Client.exe` **sin** `--e2e` apuntando al backend por `BackendSettings__BaseAddress` en un puerto libre. Teardown: matar procesos, dropear la base, restaurar settings.
- **S2 (Venta real)**: flujo completo por UI contra el stack real: login real → búsqueda de producto → agregar al carrito → cobro → pago (efectivo) → factura emitida → verificable en Historial de Ventas. Sin mock HTTP.
- **S3 (Caja y Cierre reales)**: abrir caja → operar (venta registrada) → cerrar caja; ejecutar el Cierre Diario real y verificar estado/totales coherentes con el estado real del sistema.
- **S4 (Higiene UIA)**: política de reintentos acotada para errores transitorios de UI Automation (COMException), timeouts configurables; los reintentos NUNCA enmascaran fallos de aserción. Aplicada a los puntos flaky conocidos (`PendingPickupsTests`).
- **S5 (CI)**: incorporar el E2E de escritorio al pipeline (job `wpf-e2e` en windows-2025 con PostgreSQL), build + base E2E + env, artefactos en fallo; bloqueante como el resto de gates. Local sin Postgres: retorno silencioso (patrón gated del repo); en CI ausente: falla cerrada.
- **S6 (Decisiones/supuestos)**: puerto libre detectado por el fixture y pasado a ambos procesos; `ASPNETCORE_ENVIRONMENT=Development` + `SystemSettings__AdminSeedPassword`; el admin sembrado nace con `MustChangePassword=true` → el bootstrap rota la clave por API antes de la UI; métodos de pago ya sembrados por el backend, rate y producto de prueba los crea el harness por API; el E2E full-stack convive con el modo `--e2e` existente (mock) que se conserva para health/navegación.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1-S6 | inline (orchestrator) | Doc ODD + artefactos SDD + commit de planificación | hecho — commit de planificación (hash se registra en T2) |
| T2 | S1 | delegated (writer) | Harness full-stack: fixture backend+DB+bootstrap+settings+cliente real + gating + smoke de login real | pendiente |
| T3 | S4 | delegated (writer) | Higiene UIA: helper de reintentos COM + timeouts configurables + aplicar a flaky conocidos | pendiente |
| T4 | S2 | delegated (writer) | Flujo real de venta completa + verificación en historial | pendiente |
| T5 | S3 | delegated (writer) | Flujos reales de caja y cierre diario | pendiente |
| T6 | S5 | delegated (writer) | CI: job `wpf-e2e` + artefactos + env Postgres | pendiente |
| T7 | S1-S5 | delegated (verify) + inline | Verificación independiente por slice + final + ANEXO 8.148 | pendiente |

Estrategia de entrega: `ask-on-risk` → cadena **stacked-to-main** cacheada (misma política de la cadena V0.15).

## Log

- L1 (2026-10-04) — Pedido del usuario, verbatim: "Verifica la configuracion de la pruebas E2E, necesito que las pruebas usen funciones reales del sistema no solo navagacion ventas cierre ETC, dame tu chequeo" → chequeo entregado (E2E actual = cliente real contra mock HTTP; ventas/cierre/caja son navegación) y luego: "Procede con Nivel B e Higiene" (verbatim del alcance elegido: Nivel B = full-stack real; Higiene = estabilización UIA + decisión de CI).
- L2 (2026-10-04) — Evidencia de exploración: `WpfAppFixture` lanza `Desktop.Client.exe --e2e` (mock HTTP de 909 líneas en `Desktop.Client/Services/E2EMockHttpMessageHandler.cs`; sin backend/DB/SignalR). Cliente: base address = `clientSettings.ServerBaseAddress` (`%LocalAppData%\ProyectoPOS\client_settings.json`, default `http://localhost:5000/`) con fallback a `BackendSettings:BaseAddress` (`App.xaml.cs:157-161`). Backend: `DatabaseInitializer` migra ambos DbContexts y siembra admin desde config (L109-110, L347-401) con `MustChangePassword=true`; cliente general y métodos de pago (`PaymentMethodDefaults.CreateDefault`) también sembrados; health anónimo en `/health` y `/api/health` (`HealthController.cs:37-40`, permitido por `VersionCheckMiddleware`); `auth/login` en `AuthController.cs:34`. CI actual (ci.yml): jobs backend (windows-2025 + PostgreSQL, crea `pos_test`) y frontend; E2E no corre en CI. Flakes UIA documentados (COMException en corridas completas; clase aislada 4/4).
- L3 (2026-10-04) — Plan SDD creado (proposal + 3 specs + design + tasks + state) y doc ODD. Próximo: T2 (harness full-stack) delegado a writer con verificación independiente.
