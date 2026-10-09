# Spec (delta): Anclaje de tasa en checkout-preview — SEC-08 (8.152)

Fuente: auditoría re-emitida 2026-10-08, hallazgo SEC-08: "Validar que `request.ExchangeRate` no se desvíe de la tasa oficial vigente en más del umbral de tolerancia del sistema antes de procesar la previsualización."

## ADDED Requirements

### Requirement: REQ-CPR-01 — La tasa del preview usa el mismo anclaje que el completar venta

`POST /api/sales/{id}/checkout-preview` MUST resolver la tasa efectiva como:

1. Si `request.ExchangeRate > 0`: `rate = await ISalesService.ResolveCheckoutRateAsync(id, request.ExchangeRate, cancellationToken)`.
2. Si `request.ExchangeRate <= 0`: `rate = sale.AppliedRate` (sin invocar el resolver).
3. Si la tasa resultante es `<= 0`: 400 `"Tasa de cambio inválida."` (guard existente).

`ISalesService.ResolveCheckoutRateAsync(int saleId, decimal clientRate, CancellationToken cancellationToken = default)` MUST delegar en `SalesService.ResolveAnchoredRateAsync(clientRate, contextLabel: "CheckoutPreview", referenceId: saleId)` sin duplicar reglas y sin alterar la firma ni la semántica del método privado.

Semántica heredada (idéntica a `Sales.Module/Services/SalesService.Mapping.cs:104-170`):

- `clientRate <= 0` → `InvalidOperationException` (no alcanzable desde el preview por el punto 2).
- Redondeo hacia arriba a 2 decimales de la tasa cliente (`PricingCalculator.RoundExchangeRateCeiling`).
- Sin servicio de inventario o sin tasa BCV (0 o excepción) → fail-open: tasa cliente redondeada (con log de auditoría).
- Desvío `>= 1.0` (±100%) → `ArgumentException` con mensaje exacto:
  `"La tasa de cambio {clientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({officialRate}). Contacte al supervisor."`
- Desvío `> tolerancia` → ancla a la tasa BCV del día (warning de auditoría A5-AUDIT con contexto `CheckoutPreview`).
- Tolerancia: setting `RateDeviationTolerancePct` (0 < x < 1); default `0.10`.

#### Scenario: Desvío dentro de la tolerancia

- GIVEN BCV oficial 50 y tolerancia default 0.10
- WHEN preview con `ExchangeRate = 52` (4%)
- THEN los totales se calculan con 52 (tasa del cliente).

#### Scenario: Desvío fuera de tolerancia ancla a BCV

- GIVEN BCV oficial 50 y tolerancia default 0.10
- WHEN preview con `ExchangeRate = 60` (20%)
- THEN los totales se calculan con 50 (BCV anclada) y se registra el warning A5-AUDIT con contexto `CheckoutPreview`.

#### Scenario: Desvío >= ±100% rechaza con 400

- GIVEN BCV oficial 50
- WHEN preview con `ExchangeRate = 120` (140%)
- THEN `ArgumentException` con el mensaje exacto de rechazo; el middleware global responde 400 ProblemDetails.

#### Scenario: Sin tasa BCV del día — fail-open

- GIVEN BCV no disponible (0 o excepción del servicio)
- WHEN preview con `ExchangeRate = 77`
- THEN los totales se calculan con 77.

#### Scenario: Sin tasa del cliente — fallback intacto

- GIVEN `request.ExchangeRate <= 0`
- WHEN preview
- THEN se usa `sale.AppliedRate` sin llamar al resolver.

#### Scenario: Tolerancia configurable

- GIVEN `RateDeviationTolerancePct = 0.05` y BCV 50
- WHEN preview con `ExchangeRate = 53` (6%)
- THEN los totales se calculan con 50 (ancla).

### Requirement: REQ-CPR-02 — Cobertura de pruebas del preview

- Tests service-level del resolver (`ResolveCheckoutRateAsync`) para los 5 comportamientos + tolerancia configurable.
- Tests controller-level: ajuste de los tests existentes del preview (mock passthrough) y al menos un escenario de anclaje y uno de rechazo (400/mensaje).
- Los tests existentes ajustados MUST reportarse nombradamente en el retorno del work unit.
