# Limpieza de controladores + cálculo de cobro + PERF-03

Objetivo: tramo 8.159 del roadmap de auditoría — CLEAN-04 (extraer el cálculo de cobro del controlador a `CheckoutCalculationService`), CLEAN-01 (erradicar los 93 `[NonAction]` zombis y migrar los call sites de tests) y PERF-03 (refutado con evidencia + residual `AsNoTracking`). El tramo 8.158 (SRE-03/CLEAN-03) queda pendiente. Change: `openspec/changes/2026-10-10-audit-controller-cleanup/` (ANEXO 8.159). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (CLEAN-04)**: `ICheckoutCalculationService` + `CheckoutCalculationService` (fetch + anclaje SEC-08 + matemática espejo); endpoint = auth + delegación + Ok; DI scoped; ctor opcional; errores preservados (missing → KeyNotFound 404 real; `rate <= 0` → ArgumentException 400; rechazo ±100% intacto). Detalle: `specs/checkout-calculation-service/spec.md`.
- **S2 (CLEAN-01)**: 93 wrappers eliminados (Grupo A=37: Auth/Users/PaymentMethods/Settings/ExchangeRate/Health/Receipts/Reservations; Grupo B=56: CashDrawer/DailyClosure/Products×3/Sales×7) + call sites de tests → `*Async` (token default/None) + `CancellationPropagationTests` migrado; cero cambios de contrato HTTP. Detalle: `specs/remove-nonaction-wrappers/spec.md`.
- **S3 (PERF-03)**: refutación documentada (`AsSplitQuery` en `GetSaleEntityAsync` desde 148e6a3; `asNoTracking` param; History con AsNoTracking) + residual `AsNoTracking` en el primer fetch de `ConfirmPickupAsync`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1 | delegated (writer) | CLEAN-04: servicio + controller + DI + tests | pendiente |
| T2 | S3 | inline | PERF-03: residual AsNoTracking + evidencia de refutación | pendiente |
| T3 | S2-A | delegated (writer) | CLEAN-01 grupo A (37 wrappers) + tests | pendiente |
| T4 | S2-B | delegated (writer) | CLEAN-01 grupo B (56 wrappers) + tests | pendiente |
| T5 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.159 + cierre | pendiente |

## Log

- L1 (2026-10-10) — Pedido del usuario, verbatim: "Procede con 8.159".
- L2 (2026-10-10) — Verificación previa: CLEAN-04 CONFIRMADO (~55 líneas de matemática en el endpoint; el `ApiNotFound` actual es inalcanzable porque `GetSaleAsync` lanza `KeyNotFoundException("Sale {id} not found.")` → 404 real a preservar); CLEAN-01 CONFIRMADO (93 wrappers en 19 archivos; ~245 usos en tests con falsos positivos de reflexión/mocks a triage; `CancellationPropagationTests:251-266` pinea 7 wrappers); **PERF-03 REFUTADO** (AsSplitQuery en `GetSaleEntityAsync` desde `148e6a3` + `asNoTracking` param + History ya con AsNoTracking en todas las lecturas; residual mínimo en el primer fetch de ConfirmPickup).
- L3 (2026-10-10) — Próximo: T1 delegado a writer (superficies: Sales.Module/Interfaces/ICheckoutCalculationService.cs, Sales.Module/Services/CheckoutCalculationService.cs, SalesController.cs, SalesController.Checkout.cs, ServiceCollectionExtensions.cs, Phase4FinancialIntegrityAndPreviewTests.cs, CheckoutPreviewRateAnchorTests.cs, CheckoutCalculationServiceTests.cs nuevo).
