# Spec (delta): Checkout WPF server-authoritative — CLEAN-05 (8.156)

Fuente: auditoría integral re-emitida, CLEAN-05: "Ambos clientes deben invocar exclusivamente `/api/sales/{id}/checkout-preview` y enlazar sus estados de botón ('Cobrar', 'Saldo Restante') a las propiedades devueltas por el servidor". El Web ya lo cumple (8.5-WEB1, `computeCheckoutGate` + `CheckoutPreviewGate.test.js`); este delta cierra la parte WPF.

## ADDED Requirements

### Requirement: REQ-WCP-01 — Cliente WPF del preview

`Desktop.Client.Core` MUST exponer `ISalesService.GetCheckoutPreviewAsync(int saleId, decimal exchangeRate, IEnumerable<SalePaymentDto> payments)` con su DTO de respuesta (TotalUSD, TotalBsS, TotalPaidUSD, TotalPaidBsS, RemainingBalanceUSD, RemainingBalanceBsS, RoundingAdjustment, ChangeDueUSD, ChangeDueBsS, IsFullyPaid) en el archivo de `SalePaymentDto`, haciendo POST a `/api/sales/{id}/checkout-preview` con el mismo body que el Web (`exchangeRate` + `payments` con methodId/amount/amountBsS/amountLocal/referenceNumber). Pin de contrato en `ClientHttpContractTests`.

#### Scenario: Contrato

- GIVEN el servicio cliente
- WHEN se invoca el preview
- THEN la request es POST a `/api/sales/{id}/checkout-preview` y el pin la verifica contra `SalesController.GetCheckoutPreviewAsync`.

### Requirement: REQ-WCP-02 — Gate espejo de `computeCheckoutGate`

`CheckoutViewModel` MUST espejar la semántica del Web:

- `IsPreviewFresh` = preview presente ∧ ¬fallo ∧ firma de la respuesta == firma del estado actual (saleId|tasa|pagos), con versión monotónica para descartar respuestas viejas.
- `IsFullLiquidation` = `IsPreviewFresh ∧ preview.IsFullyPaid` (ya no hay umbral local 0.05).
- `IsCustodyAllowed` = `IsFullLiquidation ∧ ¬IsDefaultCustomer`.
- `CanFinalize` = `HasValidPayments ∧ IsPreviewFresh ∧ (IsOverrideMode ? true : IsFullLiquidation) ∧ (!IsPendingPickup ∨ IsCustodyAllowed)`.
- `RoundingAdjustment` = `IsPreviewFresh ? preview.RoundingAdjustment : 0m`.
- `RemainingBalanceUsd/Local` mostrados = los del preview cuando fresco (fallback local solo de presentación mientras carga el primero).
- En fallo de la última validación: `CanFinalize=false` y mensaje exacto del Web: `"No se pudo validar el cobro con el servidor. Verifique la conexión e intente nuevamente (la transacción no puede cerrarse sin la validación canónica)."`.

El fetch se dispara desde `RecalculateBalances()` (altas/bajas de pago, cambios de total) y una vez en el ctor; el seam para tests MUST ser determinista (p. ej. propiedad `Task? PendingPreview` o método `RefreshPreviewAsync`).

#### Scenario: Fresco y completo

- GIVEN pagos válidos y preview fresco con `isFullyPaid=true`
- THEN `IsFullLiquidation/CanFinalize` true y `RoundingAdjustment` = el del servidor.

#### Scenario: Estancado

- GIVEN pagos cambiados sin respuesta aún (firma vieja)
- THEN el gate cierra (`CanFinalize=false`) hasta recibir la firma vigente.

#### Scenario: Fallo de validación

- GIVEN fallo del preview
- THEN `CanFinalize=false` y el mensaje canónico del Web aparece.

#### Scenario: Override parcial

- GIVEN venta OnHold con abono parcial y preview fresco
- THEN `CanFinalize=true` (label "REGISTRAR ABONO") y la liquidación completa usa `preview.IsFullyPaid`.

### Requirement: REQ-WCP-03 — Finalize sellado por el preview

`FinalizeSale` MUST requerir `IsPreviewFresh` (guard de entrada), usar el `RoundingAdjustment` del preview al llamar `CompleteSaleAsync`/persistir, y en override MUST branchar por `preview.IsFullyPaid` (completa → CompleteSale; parcial → abonos en hold). El toggle de custodia MUST usar `IsCustodyAllowed` del gate.

#### Scenario: Cobro normal

- GIVEN gate fresco full
- WHEN se finaliza
- THEN `CompleteSaleAsync` recibe el `RoundingAdjustment` del servidor y la venta cierra.

### Requirement: REQ-WCP-04 — Web sin cambios (ya conforme)

El Web MUST quedar intacto: consume el preview con gate testeado (8.5-WEB1); la verificación se documenta en el ANEXO (evidencia: `computeCheckoutGate`, `CheckoutPreviewGate.test.js`, uso del `roundingAdjustment` del servidor en `completeSale`).

### Requirement: REQ-WCP-05 — Cobertura

- Tests nuevos espejo del gate web (fresco/estancado/fallido/override/normal) en archivo nuevo autorizado.
- `CheckoutUxTests` ajustados al gate por preview (mock del service + await del seam), reportados nominados.
- Pin de contrato HTTP del nuevo método.
