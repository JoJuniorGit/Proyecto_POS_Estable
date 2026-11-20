# Perf integral WU1-WU3

Objetivo: optimizar build/instalador, suite de tests y arranque/UI sin cambiar reglas de negocio ni reducir cobertura.
Branch: trabajo actual. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`; E2E: `tests/CommandCenter.Wpf.E2ETests`.

## Specs

- S1. WU1: `Directory.Build.props` habilita `UseSharedCompilation`, `BuildInParallel`, `ProduceReferenceAssembly`.
- S2. WU1: `installer/setup.iss` elimina `UninstallIconFile`, usa `RunOnceId` únicos en `[UninstallRun]`, `SolidCompression=no`, `lzma2/fast`, `CompressionThreads=auto`, `LZMAUseSeparateProcess=yes` (Inno 6) y `zip/1` con `FastBuild`.
- S3. WU2: los fixtures PostgreSQL crean una base plantilla migrada una vez por proceso y clonan con `CREATE DATABASE ... TEMPLATE`; los tests de migración desde cero conservan base vacía.
- S4. WU2: xUnit ejecuta tests unitarios/InMemory en paralelo; solo colecciones con recursos físicos, puertos o estado global compartido quedan serializadas.
- S5. WU3: backend verifica migraciones antes de rutinas pesadas y los BackgroundServices no críticos tienen retardo inicial; Kestrel abre el puerto sin esperar seeds.
- S6. WU3: ningún DataGrid de Caja/Inventario/Ventas/Rentabilidad queda dentro de un ScrollViewer/StackPanel vertical que rompa virtualización; lecturas independientes usan `Task.WhenAll`.
- S7. Se mide antes/después: build incremental, instalador, suite completa y arranque del backend.
- S8. Sin cambios en RBAC, precios, cálculos financieros, contratos HTTP ni reglas de venta; sin reducir cobertura.

## Tasks

- WU1 | S1-S2 | Build/Inno | completed | build 0/0; instalador normal 41.9s / 154.9 MB; Fast 33.3s / 193.5 MB.
- WU2-T1 | S3 | Plantilla PostgreSQL | completed | 47 tests Postgres: 129.5s -> 106.4s.
- WU2-T2 | S4 | Paralelismo xUnit | completed | suite 4458 tests: 349.3s -> 209.6s (3 corridas verdes).
- WU3-T1 | S5 | Arranque backend | completed | base nueva: puerto 12.34s -> 2.19s; base migrada: puerto 5.71s -> 2.60s.
- WU3-T2 | S6 | DataGrids/ViewModels WPF | completed | virtualización y Task.WhenAll; E2E 11/11.
- WU4 | S7 | Tabla comparativa | completed | ver reporte final.

## Log

### L1 - Pedido del usuario
"Actúa como un Principal .NET Performance & Build Engineer. Vamos a ejecutar un plan de optimización integral en tres Work Units (Build/Instalador, Suite de Tests y Arranque/UI) sin alterar ninguna regla de negocio ni reducir la cobertura de pruebas: WU1 — Compilación MSBuild e Instalador Inno Setup... WU2 — Aceleración de la Suite de Pruebas (4.382 tests)... WU3 — Arranque en Frío de Backend y Fluidez WPF... Mide con cronómetro los tiempos antes y después de cada Work Unit..."

### L2 - Evidencia baseline
- `dotnet build CommandCenter.slnx -c Release`: 0/0, 1:16 con restore; incremental `--no-restore`: 11.4s.
- Suite completa con PostgreSQL local: 4458 tests, 349.3s.
- Carril PostgreSQL: 47 tests, 129.5s.
- Instalador: 41.3s normal, 33.3s Fast; 154.9 MB / 193.5 MB.

### L3 - Decisiones
- 2026-11-18: la plantilla PostgreSQL usa solo esquema migrado; el seed estándar se mantiene por test para no alterar el aislamiento ni el estado inicial esperado por cada fixture.
- 2026-11-18: Inno 7 marca obsoleto `LZMAUseSeparateProcess`; se emite solo para Inno 6 para conservar 0 warnings.
- 2026-11-18: el paralelismo xUnit serializa `CurrencyDisplayState`, `WeakMessengerState`, `PostgresReal` y `FullStackSerial`; el resto corre en paralelo.
- 2026-11-18: el arranque diferido de Kestrel es opt-in por `Startup:DeferDatabaseInitialization`; producción lo activa y Development/tests conservan el orden clásico.

### L4 - Evidencia final
- `dotnet build CommandCenter.slnx -c Release --no-restore`: 4.9s incremental, 0/0.
- Suite completa: 4458/4458, 209.6s (3 corridas verdes consecutivas).
- WPF E2E: 11/11, 50.8s.
- Instalador normal: 41.9s, 154.9 MB, 0 warnings; pipeline 97.8s.
- Backend base nueva: puerto 12.34s -> 2.19s; health 12.91s -> 11.88s.
- Backend base migrada: puerto 5.71s -> 2.60s; health 6.10s -> 5.95s.
- Durante el arranque diferido `/api/products` y `/api/health` responden 503 hasta readiness.
