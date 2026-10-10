# Spec (delta): Extracción del cálculo de cobro — CLEAN-04 (8.159)

Fuente: auditoría re-emitida, CLEAN-04: "Mover la lógica a `Sales.Module/Services/CheckoutCalculationService.cs`. El endpoint debe limitarse a delegar."

## ADDED Requirements

### Requirement: REQ-CHC-01 — El cálculo financiero vive en un servicio de dominio

`Sales.Module` MUST exponer `ICheckoutCalculationService` con `Task<CheckoutPreviewResponse> CalculatePreviewAsync(int saleId, CheckoutPreviewRequest request, CancellationToken cancellationToken = default)` implementado en `Sales.Module/Services/CheckoutCalculationService.cs`, que:

- obtiene la venta vía `ISalesService.GetSaleAsync` (missing → `KeyNotFoundException` como hoy → 404);
- resuelve la tasa con la MISMA regla del 8.152: `request.ExchangeRate > 0` → `ISalesService.ResolveCheckoutRateAsync(saleId, rate, ct)`; si no → `sale.AppliedRate`; tasa final `<= 0` → `ArgumentException("Tasa de cambio inválida.")` (400 por middleware de dominio);
- aplica la matemática ESPEJO del endpoint actual (totales, pagos mixtos con conversión por tasa, `remaining`, `isFullyPaid = remainingUsd <= 0.05`, `roundingAdjustment = remainingUsd <= 0.01 ? RoundToDigital(paidBsS - totalBsS) : 0`, vuelto `> totalUsd + 0.05`).

`SalesController.GetCheckoutPreviewAsync` MUST quedar como: check de autorización → delegación → `Ok(preview)` (sin `PricingCalculator` ni umbrales en el controlador). Registro DI scoped; el ctor de `SalesController` agrega el parámetro OPCIONAL (precedente 8.149/8.150) y los tests de preview construyen el servicio real con su mock de `ISalesService`.

#### Scenario: Comportamiento idéntico

- GIVEN los escenarios existentes de `Phase4FinancialIntegrityAndPreviewTests` y `CheckoutPreviewRateAnchorTests`
- WHEN corren contra el endpoint con el servicio real
- THEN mismos resultados, mismos mensajes y mismos status (404 por `KeyNotFoundException` en missing; 400 "Tasa de cambio inválida."; rechazo ±100% con mensaje exacto; anclaje BCV intacto).

#### Scenario: Sin lógica financiera en el controlador

- GIVEN el endpoint
- THEN no contiene `PricingCalculator`, umbrales `0.05/0.01` ni la construcción de `CheckoutPreviewResponse`.

### Requirement: REQ-CHC-02 — Cobertura

- Tests nuevos del servicio (redondeo fiscal, vuelto, pagos mixtos USD/BsS, `rate <= 0`).
- Tests existentes de preview ajustados (construyen el servicio real) y reportados nombradamente.
