# Proposal: Hardening WPF (PERF-04/05) + checkout WPF server-authoritative (CLEAN-05) — ANEXO 8.156

## Contexto

Tramo 4 autorizado ("sigue con 8.156"). Alcance: **PERF-04** (leaks de diálogos WPF por lambdas en `RequestClose`), **PERF-05** (retención de UserControls por suscripción `DataContextChanged` sin `Unloaded`) y **CLEAN-05** en su parte WPF (el WPF duplica la liquidación; el Web ya consume `/checkout-preview` desde 8.5-WEB1 con el gate testeado en `CheckoutPreviewGate.test.js`).

Verificación previa (estática, de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| PERF-04 | CONFIRMADO | `ProductDialog.xaml.cs:16-19` y `ServerConnectionDialog.xaml.cs:16-20`: lambda suscrita a `ViewModel.RequestClose` sin desuscripción (captura `this`). |
| PERF-05 | CONFIRMADO | `InventoryView.xaml.cs:16,19-29` y `LoginView.xaml.cs:14,17-27`: hook por `DataContextChanged` sin `Loaded/Unloaded`; los VMs (singleton DI) retienen el control desalojado. Patrón correcto de referencia: `DailyClosureView.xaml.cs` (hook/unhook + Loaded/Unloaded). |
| CLEAN-05 (WPF) | CONFIRMADO | `CheckoutViewModel` calcula local `IsFullLiquidation` (0.05m), `RemainingBalance*`, `RoundingAdjustment` y el branch de override; el cliente WPF NO tiene `GetCheckoutPreviewAsync` (grep 0). El Web SÍ lo consume: `computeCheckoutGate` (CheckoutModal.jsx:42-62) exige preview FRESCO por firma, bloquea sin validación canónica y usa el `roundingAdjustment` del servidor. |

## Alcance

1. **S1 (PERF-04)**: los dos diálogos guardan el delegado (`Action<bool> _closeHandler`), lo desuscriben en `OnClosed` (antes de `Dispose`) — sin lambdas anónimas vivas.
2. **S2 (PERF-05)**: `InventoryView` y `LoginView` adoptan el patrón de `DailyClosureView` (hooks en `Loaded/DataContextChanged`, unhook en `Unloaded`; `_boundViewModel` + `ReferenceEquals`), sin retención del VM tras desalojar la vista. `LoginView` sigue SIN disponer el VM (fix W11 de 8.149).
3. **S3 (CLEAN-05 WPF)**: el WPF consume `/checkout-preview` como fuente canónica:
   - Cliente: `ISalesService.GetCheckoutPreviewAsync(saleId, exchangeRate, payments)` + DTO (en el archivo de `SalePaymentDto`) + pin de contrato.
   - VM: espejo de `computeCheckoutGate` con firma fresca (`IsPreviewFresh`); `IsFullLiquidation`/`CanFinalize`/`IsCustodyAllowed`/`RoundingAdjustment` y el saldo mostrado (`RemainingBalance*`) provienen del preview cuando está fresco; `RoundingAdjustment` se envía a `CompleteSaleAsync`; el branch de override usa `preview.IsFullyPaid`; sin validación fresca NO se puede finalizar (mensaje exacto del web en fallo). Seam determinista para tests (p. ej. `PendingPreview`/`RefreshPreviewAsync`).
   - Web: verificado como YA conforme (8.5-WEB1) — sin cambios; documentado en el ANEXO con evidencia.

Fuera de alcance: PERF-01/02/03, SEC-07, CLEAN-01/03/04 y el resto del roadmap.

## Enfoque

- El fetch del preview se dispara desde `RecalculateBalances()` (altas/bajas de pago y cambios de total) con versión monotónica; la respuesta aplica solo si corresponde a la última firma; el fallo de la última marca `PreviewFailed` (gate cerrado). El ctor dispara el primer fetch (paridad con el web al abrir el modal).
- Los tests existentes de `CheckoutUxTests` se ajustan para montar el mock del preview y esperar el seam (se reportan nominados); se agregan tests espejo del gate web (fresco/estancado/fallido/override/normal).

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~500-650 líneas.
- Riesgo principal: el gate del checkout es flujo crítico de cobro — se espeja EXACTAMENTE la semántica del web ya en producción (8.5-WEB1) y los tests cubren fresco/estancado/fallido.
- RDD: off (clone-local) → verificación independiente + suite/cobertura.

## Tramos siguientes

- 8.157: SEC-07 (SignalR ForceDisconnect/IHubFilter). 8.158: SRE-03 + CLEAN-03. 8.159: CLEAN-01/04 + PERF-03.
