```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: HEAD ac00e38 + sidecar hardening (uncommitted at verification time)
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 4/5 met (1 PARTIAL: CI runtime pending next push)
scenarios: 13 (11 COMPLIANT, 2 PARTIAL CI-runtime, 0 UNTESTED, 0 FAILING)
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --nologo
test_result: 1770/1770 passed, exit 0
e2e_mock: 49/49 passed (3m28s; full-stack silent-return ~0 s in trx)
e2e_fullstack_gated: FullStackSmoke 1/1 · FullStackSale 1/1 · FullStackCashClosure 1/1
build_result: 0 warnings / 0 errors
coverage: Core 0.8835 (>=0.70) / Sales.Module 0.8893 (>=0.80) / Inventory.Module 0.8547 (>=0.72), exit 0
note: los escenarios CI-runtime (job wpf-e2e) quedan PARTIAL por diseño hasta el próximo push a V0.15; el sidecar de recuperación fue simulado end-to-end por el padre y verificado estructuralmente por el verificador final.
```

## Verification Report

**Change**: 2026-10-04-wpf-e2e-full-stack
**Version**: N/A (delta specs)
**Mode**: Standard (RDD OFF clone-local — verificación comisionada por el orquestador)

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 7 (T2–T6, T3b, T5b, T5c, T7) |
| Tasks complete | Todos |

### Spec Compliance Matrix (5 requirements / 13 scenarios)

| Requirement / Scenario | Verdict | Evidence |
|---|---|---|
| R1 Full-Stack Harness Lifecycle | MET | |
| S1.1 Harness boot and clean teardown | COMPLIANT | `FullStackSmokeTests.HarnessBoots…`; gated 1/1 (23 s); DB dropped, settings restored, teardown errors empty |
| S1.2 Gated without environment | COMPLIANT | mock run 49/49 con env ausente; `GatingDecisionTests`; `if (!IsAvailable) return;` en los 3 flujos |
| S1.3 Fail closed in CI without database | COMPLIANT | `Evaluate_WithoutConnectionStringInCi_ThrowsFailClosed` (+ theory); throw en el ctor del fixture |
| R2 Real Sale Flow | MET | |
| S2.1 Complete sale end to end | COMPLIANT | `FullStackSaleTests.CompleteSale_WithCashPayment…`; gated 1/1; factura por UI; historial por factura + total del período == cobrado |
| S2.2 No mocks in the flow | COMPLIANT | `arguments: string.Empty`; mock handler solo con `--e2e`; venta contra backend/DB reales |
| R3 Real Cash Drawer and Daily Closure Flows | MET | |
| S3.1 Cash drawer session lifecycle | COMPLIANT | `FullStackCashClosureTests…`; gated 1/1; sesión nace del cobro; CASH IN; saldo/ingresos del servidor |
| S3.2 Daily closure reflects real totals | COMPLIANT | mismo test: esperado del servidor == UI; diferencia = declarado−esperado; rollover + opening balance verificados |
| R4 Bounded UIA Retry Policy | MET | |
| S4.1 Transient COM fault recovers | COMPLIANT | `UiaRetryTests` (COM + Win32(5)) |
| S4.2 Persistent fault surfaces | COMPLIANT | `…ThrowsTheLastFaultAfterExactlyTheAttemptBudget` (`Assert.Same`) |
| S4.3 Assertions are never retried | COMPLIANT | retorno as-is; no-transitorios al primer intento; aserciones fuera de los retries |
| S4.4 Configurable timeout | COMPLIANT | `ParseFindTimeout` (+overflow) y **consumidores reales** (`FullStackSaleTests:57`, `FullStackCashClosureTests:84`) |
| R5 Desktop E2E Job in CI | PARTIAL | |
| S5.1 E2E job executes full-stack flows | PARTIAL (esperado) | definición verificada (`ci.yml:173-245`); equivalente local gated 1/1 ×3; ejecución GitHub pendiente del próximo push |
| S5.2 Failure artifacts | PARTIAL (esperado) | `upload-artifact if: failure()` (trx + logs) verificado por inspección; nunca disparado aún |

**Totals**: requirements 4/5 + 1 PARTIAL; scenarios 11/13 COMPLIANT + 2/13 PARTIAL; 0 UNTESTED; 0 FAILING.

### Residuals (categorización verificada)

**CERRADOS**: overflow `ParseFindTimeout`; consumidores de `FindTimeout`; flake "Sí" (T5b); flake `SendInput`/Win32(5) (T5c, commit UIA-first sin doble alta); sidecar persistente del settings (simulación: sentinel → smoke verde → settings byte-igual; máquina restaurada); precedencia de settings documentada.

**ACEPTADOS (rationale)**: `PerformantDataGrid` sin peer (anti-OOM; historial por búsqueda+totales); reentrancy UIA-only de `ConfirmPickupAsync` (no alcanzable por input físico); ventana de carrera del BCV job (asentamiento+alineación); fallbacks de diálogos transitorios (evidencia servidor-autoritativa); warning pre-existente Access Denied de comprobantes en ProgramData (corroborado en warn.log); ruido pre-existente de crash.log `OnClosingShutdown` (406 entradas desde 2026-09-19, ajeno).

### Notas del cierre

- `tasks.md` reconciliado 100% y `state.yaml` a `verified` (excepción del gate registrada para el archive).
- El job `wpf-e2e` se dispara en cada push a `V0.15`/PR (`ci.yml` triggers); su ejecución real es el único escenario pendiente y se resuelve con el próximo push.
