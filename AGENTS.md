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
| Integridad financiera/moneda | `docs/coding-guidelines.md` §2.4; skill `pos-financial-integrity` |
| Seguridad/RBAC/API | skill `pos-security-hardening` |
| Web React 19 / Vite | `docs/coding-guidelines.md` §3; skill `web-pos-patterns` |
| Desktop WPF / MVVM | `docs/coding-guidelines.md` §4; skill `wpf-performance-and-ui` |
| Tests | `docs/coding-guidelines.md` §6.1; skill `pos-test-automation-and-qa` |
| Arquitectura general | `ARCHITECTURE.md` |

Los skills de disciplina en `.agents/skills/` se activan según la tarea; cuando la
descripción del skill coincida con el trabajo, **debes cargarlo antes de escribir código**.

## Reglas no negociables

1. **Sin comentarios salvo que se pidan.** No añadir comentarios explicativos al código.
   (En su lugar, nombres expresivos; documentar decisiones en `docs/reporte.txt`.)
2. **Inmutabilidad del historial de ventas** (`rules.md` §1): nunca recalcular
   montos históricos con la tasa actual; usar siempre los snapshots persistidos
   (`AppliedRate`, `TotalUSD`, `FinalPaidAmountBsS`, `TotalBsS`, `RoundingAdjustment`).
3. **Aislamiento DTO**: el pipeline de datos va Entidad → DTO explícito; no exponer
   entidades de EF al cliente ni hacer doble fetch (patrón `AsSplitQuery`/proyección).
4. **Techo de tasa BCV**: redondeo hacia arriba a **4 decimales** (helper único
   `Core/Helpers/PricingCalculator.cs`); precio por unidad a centavos. Decisión 8.25-E1.
5. **Nomenclatura de tests**: `Metodo_Escenario_ResultadoEsperado`. Sin emojis.
6. **JSON en camelCase** y errores HTTP en RFC 7807 (sin filtrar `ex.Message`
   a clientes) — decisiones 8.23-C1/C2.
7. **Web**: nada de estilos inline en JSX nuevo (usar clases CSS/tokens `var(--*)`),
   Decisiones 8.24/C3. **WPF**: MVVM vía CommunityToolkit, sin UI en code-behind.
8. **Files grandes**: un componente/servicio debe caber en pantalla; dividir (anti-god-objects, guía §1.1).
9. **No inventar APIs**: antes de asumir que una clase/método existe, verificar en el
   código (fuente de verdad = repo).

## Definición de done (antes de declarar tarea completa)

- `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errores
  (`Directory.Build.props` usa `TreatWarningsAsErrors`).
- Suite .NET `dotnet test` (729) con `TEST_POSTGRES_CONNECTION` cuando aplique +
  suite web `npm test` (77) y `npm run lint` si se tocó `Web.Frontend`.
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