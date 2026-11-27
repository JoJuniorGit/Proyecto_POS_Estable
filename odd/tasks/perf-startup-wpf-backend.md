# Startup and navigation performance - Backend + WPF

Rama: `V0.15`. Entrega: work units locales; push/PR a decision del mantenedor.
Runner: `dotnet build CommandCenter.slnx -c Release` + `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`.
Estrategia de entrega: `ask-on-risk`.
Objetivo: reducir tiempo de arranque en frio y fluidez de cambio de vistas sin alterar reglas de negocio.

## Specs

### S1 - Instrumentacion medible
Un `StartupTimeline` central, habilitado con `POS_STARTUP_TIMING=1`, registra marcas monotonicas con `Stopwatch` en backend y WPF. Sin la variable no agrega salida ni costo relevante. Marcas: build/pipeline/DB/Kestrel en backend; host/version/sesion/MainWindow/display/navegacion en WPF.

### S2 - Primer paint WPF
La validacion de version y la restauracion de sesion siguen siendo gates antes de habilitar login. La carga de formato de moneda y perfil monetario se mueve **despues** de `MainWindow.Show()` y corre en paralelo; los bindings se refrescan con los mensajes existentes. El usuario ve Login sin esperar hasta 4 s adicionales de red.

### S3 - Waterfalls eliminados
Se paralelizan cargas independientes en `SettingsViewModel`, `PendingOrdersViewModel`, `StockCountViewModel` y en la inicializacion de `PosViewModel`. Las dependencias reales (sesion antes de balances, venta antes de checkout) conservan su orden.
La tasa de cambio se carga una sola vez en la transicion Login -> POS: `MainViewModel` deja de esperarla para POS; `PosViewModel` es el dueno de esa carga inicial.

### S4 - Virtualizacion efectiva
Los `DataGrid` grandes de `ProfitabilityView` y `StockCountView` reciben altura maxima finita para que su `ScrollViewer` interno pueda virtualizar filas aun dentro del scroll de pagina. No se rediseña la composicion visual ni se cambian bindings.

### S5 - Jobs de arranque
`CreditMoraJob` y `AuthorizationExpiryJob` esperan una ventana inicial antes de su primer barrido para no competir con Kestrel y el primer request. BCV/Outbox conservan sus delays actuales.

### S6 - Seguridad y reglas intactas
No se toca RBAC, validacion de version, restauracion de sesion, calculos financieros, contratos HTTP ni reglas de precios. Los timeouts existentes se conservan o mejoran, nunca se eliminan.

## Tasks

- PERF-WU0 | S1 | `StartupTimeline` + marcas backend/WPF | delegated | completed (env `POS_STARTUP_TIMING=1`)
- PERF-WU1 | S2 | Mover currency/profile despues de `MainWindow.Show` y paralelizar | delegated | completed
- PERF-WU2 | S3 | Paralelizar Settings/PendingOrders/StockCount/Pos/CashDrawer + dedupe tasa | delegated | completed
- PERF-WU3 | S4 | `MaxHeight` + scroll settings en DataGrids de Profitability/StockCount | delegated | completed
- PERF-WU4 | S5 | Delay inicial configurable de CreditMora/AuthorizationExpiry | delegated | completed
- PERF-WU5 | S1-S6 | Build 0/0, tests focalizados y resumen de evidencia | delegated | completed
- PERF-WU6 | S3/S6 | Cache de vistas del shell en `MainWindow` + no cargar tasa/SignalR antes de Login + quitar probe duplicado | delegated | completed
- PERF-WU7 | S1-S6 | Verificacion final (build, E2E, suite completa) | delegated | completed

### Deferred (requieren otra fase)
- Backend startup: mover migraciones/convergencia/seeds detras de readiness gate.
- Lazy composition completa de `MainViewModel` (18 ViewModels eager); queda la infraestructura `IServiceProvider` preparada.
- Rediseno de `ProfitabilityView`/`StockCountView` sin `ScrollViewer > StackPanel`; en esta fase se mitigo con altura finita.

## Log

### L1 - Pedido del usuario
"procede" sobre el plan de optimizacion de arranque Backend/WPF y fluidez de cambio de vistas.

### L2 - Decisiones
- 2026-11-17: Se implementa primero la porcion de bajo riesgo y alto impacto: instrumentacion, primer paint, waterfalls, virtualizacion y warm-up de jobs.
- 2026-11-17: No se mueven migraciones/seeds ni se hace lazy composition completa de `MainViewModel` en este work unit; requieren una fase con pruebas de integracion y readiness gate.
- 2026-11-17: No se elimina ningun archivo.

### L3 - Evidencia de implementacion
- `dotnet build CommandCenter.slnx -c Release`: 0 warnings, 0 errors.
- WPF E2E: 11/11.
- Suite .NET completa: 4398 superados; 60 fallos exclusivamente del carril PostgreSQL real por falta de `TEST_POSTGRES_CONNECTION`.
- Marcas `StartupTimeline`: `POS_STARTUP_TIMING=1`; sin la variable no hay salida.
- Primer paint WPF: formato/perfil se cargan despues de `MainWindow.Show()` y en paralelo.
- Waterfalls: Settings (7 lecturas), PendingOrders (tasa+lista), StockCount (categorias+sesiones), Pos (tasa+metodos) y CashDrawer (historial+balances+saldo) usan `Task.WhenAll` donde las dependencias lo permiten.
- Tasa: `MainViewModel` ya no espera la tasa para POS; `PosViewModel` la carga una vez.
- Virtualizacion: `ProfitabilityView` y `StockCountView` tienen `MaxHeight` finito + `ScrollViewer.CanContentScroll` + `VirtualizingPanel.ScrollUnit=Pixel`.
- Navegacion: `MainWindow` cachea una vista por ViewModel y reutiliza el arbol ya cargado; el fallback conserva DataTemplates implicitos.
- Pre-login: `ExchangeRateService` ya no pide tasa ni abre SignalR sin sesion; `LoginViewModel` ya no duplica el probe de `ConnectionManager`.
- Jobs: `CreditMoraJob` espera 10 s; `AuthorizationExpiryJob` espera 5 s configurable (`Authorization:StartupDelaySeconds`); tests configuran 0.
