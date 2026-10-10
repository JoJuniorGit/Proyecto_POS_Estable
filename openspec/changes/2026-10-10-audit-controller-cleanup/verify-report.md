# Verify Report: Limpieza de controladores + cálculo de cobro + PERF-03 (8.159)

## Veredicto

**PASS WITH WARNINGS** — 3/3 requisitos COMPLIANT (REQ-CHC-01/02, REQ-NAC-01/02, S3). Sin CRITICAL ni WARNING funcional. Las advertencias son gaps de evidencia: W1 (los RED compile-level 72/114 no son re-ejecutables — corroborados estáticamente: 72/114 líneas removidas referencian exactamente los 37/56 wrappers) y W2 (el mapeo 404/400 del preview no tiene test HTTP — tampoco lo tenía antes; cadena estática verificada). INFOs I1-I5 documentados.

## Gates de cierre

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias, 0 errores**.
- Suite completa **CON PostgreSQL real**: **2216/2216**, 0 fallas, 0 omitidas (2207 base + 9 tests nuevos del servicio; sin tests perdidos: `Assert`/`[Fact]` netos 0 en los 39 archivos migrados).
- Cobertura CON Postgres (corrida del padre al cierre): Core **0.8886** / Sales.Module **0.9065** / Inventory.Module **0.8621** (exit 0).
- `[NonAction]` en `Backend.API` = **0** (grep global); web/bundle sin cambios en el tramo (`git diff` vacío).
- `git status` limpio en HEAD `11ef283`.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-CHC-01 | COMPLIANT | Comparación mecánica del servicio vs endpoint pre-extracción (`d52a70f^`): fórmulas/umbrales idénticos; únicas diferencias = `id→saleId` y `return ApiBadRequest` → `throw ArgumentException` (mismo mensaje, 400 por dominio). Endpoint sin `PricingCalculator`/umbrales/`new CheckoutPreviewResponse`; auth intacta; DI scoped; ctor opcional. |
| REQ-CHC-02 | COMPLIANT | 5 constructores de Phase4 pasan el servicio REAL con su mock; 9 tests nuevos del servicio (redondeo/vuelto/pagos mixtos/`rate<=0`/missing/anclaje); `CheckoutPreviewRateAnchorTests` no requería cambios (testea el resolver directo). |
| REQ-NAC-01 | COMPLIANT | 37 (T3) + 56 (T4) wrappers eliminados; diffs de los 19 controladores con **0 líneas `+`**; rutas/atributos/firmas `*Async` intactos; grep global = 0. |
| REQ-NAC-02 | COMPLIANT | 39 archivos de test con equivalencia 1:1 (normalizando `Async` → 0 mismatches; renames puros, argumentos idénticos); `CancellationPropagationTests` lista los `*Async`; `ClientHttpContractTests`/harness sin cambios necesarios; conteos 2207→2216 (solo los 9 nuevos). |
| S3 / PERF-03 | COMPLIANT | REFUTADO con evidencia: `AsSplitQuery` en `GetSaleEntityAsync` desde `148e6a3` (+4 líneas, verificado con `git log -S`), param `asNoTracking` usado por `GetSaleAsync`, History con 6× `AsNoTracking`; residual T2 = 2 líneas en el primer fetch de `ConfirmPickupAsync` (solo validación). |

## Hallazgos (honestos)

- **W1 (evidencia)**: los RED compile-level (T3: 72 CS1061; T4: 114) existieron en estados transitorios nunca versionados (commits atómicos); corroboración estática exacta (72/114 call lines = 37/56 nombres). No reproducibles sin mutar el repo.
- **W2 (cobertura de test preexistente)**: el 404 (`KeyNotFoundException` de `GetSaleAsync`) y el 400 (`ArgumentException` de dominio) del preview no tienen test HTTP; la cadena middleware (`GlobalExceptionHandlerMiddleware` → `IsDomainException` por assembly `Sales*`) quedó verificada estática; los unit tests pinean los mensajes exactos. Recomendación G1.
- **I1**: el commit de T4 se creó durante la verificación (el padre commitea por unidad; estado final congelado y fiel al working tree).
- **I2**: el `!` del null-forgiving justificado (DI scoped + 5 tests con servicio real; NRE solo en una construcción directa futura que omita el dep).
- **I3**: `CheckoutPreviewRateAnchorTests` sin cambios por diseño (resolver directo).
- **I5**: focused no re-ejecutados por el verificador (la suite completa los supersede).

## Auditoría de tests ajustados (sin enmascaramiento)

- 39 archivos migrados (17 T3 + 22 T4): reemplazos línea-por-línea idénticos salvo el sufijo `Async` (verificación programática con `Compare-Object` → 0 mismatches); `Assert` −x/+x exactos; cero tests eliminados.
- Falsos positivos respetados; los `nameof(Controller.Wrapper)` SÍ se migraron (son referencias de compilación); reflexiones por string y mocks homónimos intactos.

## Limitaciones

- W1/W2 arriba; focused no re-ejecutados; suite del verificador en Debug (el build Release 0/0 verificado aparte).

## Artefactos / commits

Change `2026-10-10-audit-controller-cleanup`; tracker `odd/tasks/auditoria-limpieza-controladores.md`; ANEXO 8.159. Commits: `5ce8b86` (plan) + `d52a70f` (T1) + `e4c2401` (T2) + `aaa01ce` (T3) + `11ef283` (T4) + commit de cierre.
