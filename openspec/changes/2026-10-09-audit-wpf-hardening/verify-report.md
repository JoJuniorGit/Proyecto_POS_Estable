# Verify Report: Hardening WPF + checkout server-authoritative (8.156)

## Veredicto

**PASS WITH WARNINGS** — 10/10 requisitos COMPLIANT (REQ-WVL-01/02/03, REQ-WCP-01..05). Los warnings de la verificación fueron atendidos: **W1** (el mock E2E no servía `/checkout-preview` → gate del checkout cerrado en modo `--e2e`) **cerrado por T2b con RED→GREEN**; **W2** (sin test del call-path `FinalizeSale`→`CompleteSaleAsync`) **cerrado por T2b** con test dedicado; **I2** (checkbox de custodia sin paridad con el web) corregido en XAML; **I1** documentado como fuera de la clase del hallazgo (asignación, no suscripción; VM local).

## Gates de cierre (post-T2b)

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias, 0 errores**.
- Suite completa **CON PostgreSQL real** (verificación T3): **2178/2178**; tras T2b (mock + XAML + 2 tests): **2180/2180**, 0 fallas, 0 omitidas.
- Cobertura CON Postgres (post-T2b, coverage.runsettings + `check-coverage.py`, exit 0): Core **0.8886** / Sales.Module **0.9057** / Inventory.Module **0.8621**.
- Web intacto: `git diff 520b943..HEAD --stat -- Web.Frontend` = vacío (conformidad 8.5-WEB1 documentada).
- Spot-check del padre: diffs T1/T2/T2b revisados; RED del mock observado en ambos sentidos.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-WVL-01 | COMPLIANT | 3 diálogos con campo `Action<bool>` + `-=` en `OnClosed` ANTES de `Dispose`; grep sin lambdas suscritas; `AuthorizationWaitDialog`/`InterruptedTransactionDialog` ya conformes. |
| REQ-WVL-02 | COMPLIANT | `InventoryView`/`LoginView` idénticos a `DailyClosureView` (hook/unhook idempotente, Loaded/DC/Unloaded); `LoginView` sin `Dispose` del singleton; sin doble suscripción. |
| REQ-WVL-03 | COMPLIANT | Build 0/0 + focused ciclo de vida 12/12; excepción test-first divulgada (code-behind sin seam; E2E gated). |
| REQ-WCP-01 | COMPLIANT | Firma + DTO (10 campos) + POST `/api/sales/{id}/checkout-preview`; body con `paymentMethodId/amount/amountBsS/amountLocal/referenceNumber`; pin mecánico de ruta/método + claves. |
| REQ-WCP-02 | COMPLIANT | Gate 1:1 con `computeCheckoutGate`: `IsPreviewFresh` (firma + versión monotónica en éxito Y fallo), `IsFullLiquidation`, `IsCustodyAllowed`, `CanFinalize`, `RoundingAdjustment`, `RemainingBalance*` del preview; mensaje canónico EXACTO; fetch único en ctor + en `RecalculateBalances`; seam `PendingPreview`. |
| REQ-WCP-03 | COMPLIANT (W2 cerrado) | Guard + `RoundingAdjustment` del servidor a `CompleteSaleAsync`/persistencia; branch override por `preview.IsFullyPaid`; ahora con test del call-path (T2b). |
| REQ-WCP-04 | COMPLIANT | Web sin cambios; evidencia de conformidad (gate + tests 8.5-WEB1). |
| REQ-WCP-05 | COMPLIANT | 6 tests del gate (5 + call-path) + mock E2E + 8 `CheckoutUxTests` ajustados + pin de contrato. |

## Hallazgos (honestos)

- **W1 — CERRADO por T2b (`0427309`)**: `E2EMockHttpMessageHandler` devolvía `{}` para el preview → `IsFullyPaid=false` → "COBRAR Y FINALIZAR" inhabilitado en modo `--e2e`. Fix: caso `checkout-preview` con preview canónico derivado del request + venta mock; test `E2EMockCheckoutPreviewTests` (RED sin el caso → GREEN con él).
- **W2 — CERRADO por T2b**: test `FinalizeSale_SendsServerRoundingAdjustment_ToCompleteSale` (verifica `CompleteSaleAsync` con el rounding `0.35` del preview y la key).
- **I1 — documentado, sin cambio**: `VariantSelectionDialog`/`VariantManagementDialog` usan **asignación** (`RequestClose = ...`) sobre un VM creado localmente por `WpfDialogService` → ciclo VM↔diálogo colectable; NO es la clase de PERF-04 (lista de suscripción que retiene al VM vivo). `CreateInvoiceProductDialog` sí usaba `+=` y fue incluido en T1.
- **I2 — corregido en T2b**: checkbox de custodia con `IsEnabled="{Binding IsCustodyAllowed}"` (paridad con el web; sin bypass previo porque `CanFinalize` sella).
- **I3 — limitaciones**: sin test unitario de vistas (excepción divulgada); `GetCheckoutPreviewAsync` sin `CancellationToken` (patrón de lecturas del cliente); firma recomputada por getter (costo menor, correctitud intacta); `saleId<=0` → `PendingPreview=CompletedTask` y gate cerrado hasta trigger.

## Auditoría de tests ajustados (sin enmascaramiento)

Los 8 `CheckoutUxTests` conservaron todas sus aserciones (diff solo `SetupPreviewMirror` + `await AwaitPreviewAsync` + async); el mock espejo deriva la respuesta del request real (crítico para el guard de sobrepago). Los tests del gate prueban propiedades reales (el `0.35` solo puede venir del preview). El pin de contrato es mecánico (plantilla por reflexión) + claves del body.

## Artefactos / commits

Change `2026-10-09-audit-wpf-hardening`; tracker `odd/tasks/auditoria-wpf-hardening.md`; ANEXO 8.156. Commits: `36ca38d` (plan) + `28e32da` (T1) + `1540a27` (T2) + `0427309` (T2b) + commit de cierre.
