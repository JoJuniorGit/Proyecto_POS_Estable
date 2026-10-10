# Limpieza de controladores + cálculo de cobro + PERF-03

Objetivo: tramo 8.159 del roadmap de auditoría — CLEAN-04 (extraer el cálculo de cobro del controlador a `CheckoutCalculationService`), CLEAN-01 (erradicar los 93 `[NonAction]` zombis y migrar los call sites de tests) y PERF-03 (refutado con evidencia + residual `AsNoTracking`). El tramo 8.158 (SRE-03/CLEAN-03) queda pendiente. Change: `openspec/changes/2026-10-10-audit-controller-cleanup/` (ANEXO 8.159). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (CLEAN-04)**: `ICheckoutCalculationService` + `CheckoutCalculationService` (fetch + anclaje SEC-08 + matemática espejo); endpoint = auth + delegación + Ok; DI scoped; ctor opcional; errores preservados (missing → KeyNotFound 404 real; `rate <= 0` → ArgumentException 400; rechazo ±100% intacto). Detalle: `specs/checkout-calculation-service/spec.md`.
- **S2 (CLEAN-01)**: 93 wrappers eliminados (Grupo A=37: Auth/Users/PaymentMethods/Settings/ExchangeRate/Health/Receipts/Reservations; Grupo B=56: CashDrawer/DailyClosure/Products×3/Sales×7) + call sites de tests → `*Async` (token default/None) + `CancellationPropagationTests` migrado; cero cambios de contrato HTTP. Detalle: `specs/remove-nonaction-wrappers/spec.md`.
- **S3 (PERF-03)**: refutación documentada (`AsSplitQuery` en `GetSaleEntityAsync` desde 148e6a3; `asNoTracking` param; History con AsNoTracking) + residual `AsNoTracking` en el primer fetch de `ConfirmPickupAsync`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1 | delegated (writer) | CLEAN-04: servicio + controller + DI + tests | hecho — 5 constructores ajustados + 9 tests nuevos; focused 28/28; suite 2216/2216; commit d52a70f |
| T2 | S3 | inline | PERF-03: residual AsNoTracking + evidencia de refutación | hecho — AsSplitQuery desde 148e6a3; focused 62/62; suite 2216/2216; commit e4c2401 |
| T3 | S2-A | delegated (writer) | CLEAN-01 grupo A (37 wrappers) + tests | hecho — 72 call sites en 17 archivos (RED 72 CS1061); focused 478/478; suite 2216/2216; commit aaa01ce |
| T4 | S2-B | delegated (writer) | CLEAN-01 grupo B (56 wrappers) + tests | hecho — 114 call sites en 22 archivos (RED 114 errores); [NonAction]=0 global; focused 356/356; suite 2216/2216; commit 11ef283 |
| T5 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.159 + cierre | hecho — PASS WITH WARNINGS (3/3 COMPLIANT; W1/W2 + I1-I5); cobertura 0.8886/0.9065/0.8621 exit 0; ANEXO 8.159; commit de cierre en L6 |

## Log

- L1 (2026-10-10) — Pedido del usuario, verbatim: "Procede con 8.159".
- L2 (2026-10-10) — Verificación previa: CLEAN-04 CONFIRMADO (~55 líneas de matemática en el endpoint; el `ApiNotFound` actual es inalcanzable porque `GetSaleAsync` lanza `KeyNotFoundException("Sale {id} not found.")` → 404 real a preservar); CLEAN-01 CONFIRMADO (93 wrappers en 19 archivos; ~245 usos en tests con falsos positivos de reflexión/mocks a triage; `CancellationPropagationTests:251-266` pinea 7 wrappers); **PERF-03 REFUTADO** (AsSplitQuery en `GetSaleEntityAsync` desde `148e6a3` + `asNoTracking` param + History ya con AsNoTracking en todas las lecturas; residual mínimo en el primer fetch de ConfirmPickup).
- L3 (2026-10-10) — Próximo: T1 delegado a writer (superficies: Sales.Module/Interfaces/ICheckoutCalculationService.cs, Sales.Module/Services/CheckoutCalculationService.cs, SalesController.cs, SalesController.Checkout.cs, ServiceCollectionExtensions.cs, Phase4FinancialIntegrityAndPreviewTests.cs, CheckoutPreviewRateAnchorTests.cs, CheckoutCalculationServiceTests.cs nuevo).
- L4 (2026-10-10) — T1 (d52a70f, CLEAN-04) y T2 (e4c2401, PERF-03 residual) completados: servicio con matemática verbatim + anclaje SEC-08; endpoint = auth + delegación; DI scoped; ctor opcional; errores preservados (404 real por KeyNotFound; 400 por ArgumentException de dominio); 5 constructores de tests ajustados (reportados) + 9 tests nuevos; RED compile-level → focused 28/28; suite 2216/2216. T2: AsNoTracking en el primer fetch de ConfirmPickup (solo validación) + evidencia de refutación (AsSplitQueue desde 148e6a3; History ya con AsNoTracking); focused 62/62.
- L5 (2026-10-10) — T3 (aaa01ce, grupo A: 37 wrappers + 72 call sites en 17 archivos; RED 72 CS1061) y T4 (11ef283, grupo B: 56 wrappers + 114 call sites en 22 archivos; RED 114 errores; `CancellationPropagationTests` lista los `*Async`) completados; `[NonAction]` = 0 global; focused 478/478 y 356/356; suite 2216/2216 en ambos.
- L6 (2026-10-10) — Cierre: verificación independiente PASS WITH WARNINGS (3/3 requisitos COMPLIANT; comparación mecánica del servicio vs endpoint pre-extracción; equivalencia 1:1 en 39 archivos de tests; W1 REDs no reproducibles con corroboración estática; W2 404/400 sin test HTTP preexistente; I1-I5) + verify-report.md + ANEXO 8.159 + state/tasks actualizados + commit de cierre (hash en git log). Suite CON Postgres 2216/2216; cobertura Core 0.8886 / Sales 0.9065 / Inventory 0.8621 (exit 0). Entrega pendiente: push/PR/merge (decisión del mantenedor; cadena stacked-to-main cacheada). Roadmap: queda SOLO el tramo 8.158 (SRE-03 + CLEAN-03) para cerrar la matriz completa de la auditoría re-emitida.
