# AGENTS.md — CommandCenter POS (V0.1)

Contrato operativo para agentes que crean o modifican código en este repositorio.
Este archivo se inyecta automáticamente en toda sesión de opencode. No reemplaza las
guías: las **activa** y exige su lectura antes de escribir código.

## Referencias obligatorias (leer antes de programar)

| Área de cambio | Fuente de verdad |
| --- | --- |
| Todo cambio de negocio/arquitectura | `docs/coding-guidelines.md` (guía completa) y `docs/reporte.txt` (append-only, histórico de decisiones/ANEXOS) |
| Reglas de integridad de sistema (prioridad MÁXIMA) | `rules.md` — inmutabilidad de historial, anclaje de tasas, aislamiento de snapshots |
| Backend .NET/EF/PostgreSQL | `docs/coding-guidelines.md` §2; skill `efcore-postgres-concurrency` |
| Arquitectura por capas / Endpoints REST | skill `clean-architecture`; skill `csharp-endpoints` |
| Integridad financiera/moneda | `docs/coding-guidelines.md` §2.4; skill `pos-financial-integrity` |
| Seguridad/RBAC/API | skill `pos-security-hardening` |
| Web React 19 / Vite | `docs/coding-guidelines.md` §3; skill `web-pos-patterns` |
| Desktop WPF / MVVM | `docs/coding-guidelines.md` §4; skill `wpf-performance-and-ui` |
| Tests | `docs/coding-guidelines.md` §6.1; skill `pos-test-automation-and-qa` |
| Arquitectura general | `ARCHITECTURE.md` |

Los skills de disciplina en `.agents/skills/` se activan según la tarea; cuando la
descripción del skill coincida con el trabajo, **debes cargarlo antes de escribir código**.
Las skills genéricas conservadas (`aspnet-core`, `csharp-async`, `csharp-xunit`,
`accessibility`) quedan subordinadas a las skills del proyecto y a `docs/coding-guidelines.md`.

## Reglas no negociables

1. **Sin comentarios salvo que se pidan.** No añadir comentarios explicativos al código.
   (En su lugar, nombres expresivos; documentar decisiones en `docs/reporte.txt`.)
2. **Inmutabilidad del historial de ventas** (`rules.md` §1): nunca recalcular
   montos históricos con la tasa actual; usar siempre los snapshots persistidos
   (`AppliedRate`, `TotalUSD`, `FinalPaidAmountBsS`, `TotalBsS`, `RoundingAdjustment`).
3. **Aislamiento DTO**: el pipeline de datos va Entidad → DTO explícito; no exponer
   entidades de EF al cliente ni hacer doble fetch (patrón `AsSplitQuery`/proyección).
4. **Techo de tasa BCV**: redondeo hacia arriba a **2 decimales** (helper único
   `Core/Helpers/PricingCalculator.cs`); precio por unidad a centavos (decisiones 8.25-E1/8.102).
   La tasa **redondeada es la referencia absoluta** para todo cálculo: normalizada en
   escrituras (`ExchangeRateWriteService`) y en las lecturas que alimentan cálculos
   (`GetToday`, `ExchangeRateResolver`, `GetTodayExchangeRateAsync`); `GetHistory` muestra el log crudo (8.103).
   **Precio unitario Bs.S como ley** (8.104): Ceiling a 2 decimales
   (`PricingCalculator.ToBsSCeiling`) en modal, cliente, catálogo, backend y motor de ventas;
   `SubtotalBsS = RoundToDigital(cantidad * UnitPriceBsS)`.
5. **Nomenclatura de tests**: `Metodo_Escenario_ResultadoEsperado`. Sin emojis.
6. **JSON en camelCase** y errores HTTP en RFC 7807 (sin filtrar `ex.Message`
   a clientes) — decisiones 8.23-C1/C2.
7. **Web**: nada de estilos inline en JSX nuevo (usar clases CSS/tokens `var(--*)`),
   Decisiones 8.24/C3. **WPF**: MVVM vía CommunityToolkit, sin UI en code-behind.
8. **Files grandes**: 300-500 líneas por archivo, complejidad ciclomática ≤10; un
   componente/servicio debe caber en pantalla y dividirse en clase parcial/sub-servicio
   (anti-god-objects, guía §1.1).
9. **No inventar APIs**: antes de asumir que una clase/método existe, verificar en el
   código (fuente de verdad = repo).
10. **RBAC**: roles `Cashier`, `Manager`, `Admin`, `Driver`; `Driver` bloqueado en ventas,
    caja y cierres (guía §1 "Seguridad por Defecto"; skill `pos-security-hardening`).
11. **Dinero**: solo `decimal` para montos, subtotales, comisiones y tasas; prohibido
    `float`/`double` (guía §2.4; skill `pos-financial-integrity`).
12. **Async**: prohibido `async void` en servicios, jobs y controladores (`async Task`/
    `ValueTask`; en UI `SafeFireAndForget`) (guía §2.2).
13. **Multi-branch**: hoy sucursal única; no introducir `BranchId` hasta confirmar el
    despliegue multitienda (decisión 8.25-E3; guía §5).

## Mapa de `docs/coding-guidelines.md` (resumen no normativo)

Ante cualquier divergencia, prevalece la guía completa.

| Sección | Regla clave |
| --- | --- |
| §1 Filosofía / anti-god objects | 300-500 líneas por archivo, SRP, complejidad ciclomática ≤10; MediatR solo para cruces entre módulos |
| §2.1-2.2 Backend | `PascalCase`/`camelCase`/`_camelCase`, file-scoped namespaces, async + `CancellationToken`, prohibido `async void` |
| §2.3 Persistencia | `AsNoTracking` en lecturas, `AsSplitQuery` en colecciones múltiples, `xmin` para concurrencia, transacciones coordinadas |
| §2.4 Integridad financiera | `decimal` obligatorio, snapshots inmutables, techo BCV a 2 decimales, precio unitario Bs.S con `ToBsSCeiling` (8.104) |
| §2.5 Errores HTTP | `ProblemDetails` (RFC 7807) vía `GlobalExceptionHandlerMiddleware`; sin filtrar `ex.Message` |
| §3 Web | DTOs camelCase, sin estilos inline, tokens `var(--*)` |
| §4 WPF | MVVM con CommunityToolkit, `IDisposable`/`OnClosed`, virtualización, mensajes en español formal |
| §5 Multi-branch | Intención futura; sin `BranchId` hoy (8.25-E3) |
| §6 QA/CI | Nomenclatura `Metodo_Escenario_ResultadoEsperado`, cobertura Core ≥0.70 / Sales ≥0.80 / Inventory ≥0.72, 0 warnings |

## Definición de done (antes de declarar tarea completa)

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errores
  (`Directory.Build.props` usa `TreatWarningsAsErrors`).
- Suite .NET `dotnet test` con `TEST_POSTGRES_CONNECTION` cuando aplique + suite web
  `npm test` y `npm run lint` si se tocó `Web.Frontend`: 100% verde (conteo vigente en
  `docs/reporte.txt`).
- Gate de cobertura por capas de dominio (`python scripts/check-coverage.py <reporte>`):
  Core ≥ 0.70, Sales.Module ≥ 0.80, Inventory.Module ≥ 0.72 (decisión 8.26-E4;
  medición de dominio, excluye `*.Migrations.*`).
- Cambios de modelo persistente acompañados de **migración EF** verificada por el
  smoke `MigratedSchema` (`MigrateAsync`).
- Decisiones/resultados relevantes registrados como nuevo ANEXO al final de
  `docs/reporte.txt` (append-only).
- Commit con estilo `feat(8.xx)/fix/refactor(8.xx): ...` mencionando la revisión del
  ANEXO que lo soporta.

## Flujo de trabajo recomendado

1. Leer la sección de la guía y el skill del área antes de escribir código.
2. Para cambios candidatos a decisión de negocio/arquitectura, detallar opciones y
   preguntar al usuario antes de implementar (no inventar decisiones).
3. Tras implementar, verificar la sección "Definición de done". Para cambios no
   triviales, ejecutar el subagente `reviewer` (`.opencode/agent/reviewer.md`) y
   corregir los hallazgos bloqueantes antes de commitear.
