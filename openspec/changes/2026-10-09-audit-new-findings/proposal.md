# Proposal: Remediación de hallazgos NUEVOS de la auditoría integral re-emitida (ANEXO 8.152)

## Contexto

`docs/auditoria_integral_fases_1_4.txt` fue re-emitida por el auditor con fecha 2026-10-08 (reemplaza la versión 2026-10-05 verificada el 2026-10-05). La re-emisión:

- Confirma remediados en 8.149: SEC-01, SEC-02, CLEAN-02, SRE-01 (ventas), SRE-02 (items/caja/cierre).
- Agrega TRES hallazgos que no existían en la versión anterior: **SEC-08**, **CLEAN-06**, **SRE-04**.
- Mantiene los 17 abiertos ya conocidos (roadmap Fases 2/3 registrado en el ANEXO 8.149), que este change NO alcanza.

Pedido del mantenedor (L1 del tracker): corregir los hallazgos nuevos de esa auditoría.

## Verificación previa (estática, de este plan)

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| SEC-08 | CONFIRMADO | `Backend.API/Controllers/SalesController.Checkout.cs:30` acepta `request.ExchangeRate > 0` sin anclaje; el completar venta SÍ ancla (`Sales.Module/Services/SalesService.Checkout.cs:98` → `SalesService.Mapping.cs:104`). |
| CLEAN-06 | CONFIRMADO (referencias de línea de la auditoría con drift) | 14 catches operativos reales enumerados en S2; `PosViewModel` ya conforme; swallows tipados de ciclo de vida fuera del hallazgo. |
| SRE-04 | REFUTADO | `Inventory.Module/Data/InventoryDbContext.cs:122-131` configura `xmin` como concurrency token de `Product` (Npgsql); migraciones `20260906171036_AddXminConcurrencyTokenToProduct` y `20260908152110_RemoveRowVersionFromProduct`; `ProductConcurrencyTokenTests` (modelo + conflicto stale); el catch de `SupplierInvoiceService.Apply.cs:186` NO es código muerto (suite 2038/2038 con Postgres real en 8.150/8.151). |

## Alcance

1. **S1 (SEC-08)** — Anclaje de tasa en checkout-preview: el preview resuelve la tasa con la MISMA semántica del completar venta (≤ tolerancia → tasa cliente; > tolerancia → BCV anclada; ≥ ±100% → rechazo 400 con mensaje exacto; sin BCV → fail-open; sin tasa cliente → `sale.AppliedRate`).
2. **S2 (CLEAN-06)** — Observabilidad de catches en ViewModels: los 14 catches operativos de los 4 archivos nombrados registran en `ClientStateLogger.LogError` y conservan/proveen notificación por `IDialogService`; los swallows tipados de ciclo de vida permanecen documentados.
3. **S3 (SRE-04)** — Refutación documentada con evidencia; sin cambio de código.

Fuera de alcance: los 17 abiertos ya conocidos (fases 2/3), SEC-01 residual de céntimos, CLEAN-04/05, PERF-03 y cualquier refactor no exigido por los hallazgos nuevos.

## Enfoque S1

- Nuevo método público `ISalesService.ResolveCheckoutRateAsync(int saleId, decimal clientRate, CancellationToken cancellationToken = default)` que delega en el resolver privado existente `SalesService.ResolveAnchoredRateAsync(clientRate, contextLabel: "CheckoutPreview", referenceId: saleId)` — sin duplicar lógica ni alterar la semántica del privado.
- `SalesController.Checkout.cs` (líneas 30-31): si `request.ExchangeRate > 0` → resolver; si no → `sale.AppliedRate` (comportamiento actual). Guard `rate <= 0` → 400 "Tasa de cambio inválida." intacto. El `ArgumentException` del rechazo ±100% fluye al middleware global (`GlobalExceptionHandlerMiddleware`) → 400 ProblemDetails.
- Sin cambios en clientes (WPF/Web): consumen el preview; solo cambia el resultado ante desvío > tolerancia o rechazo.

## Enfoque S2

- 14 sitios exactos (tabla en `specs/viewmodel-error-observability/spec.md`): BaseViewModel (2), InventoryViewModel.cs (3), InventoryViewModel.Operations.cs (8), ProductDialogViewModel (1).
- Patrón espejo de CLEAN-02 (CartViewModel): `ClientStateLogger.LogError($"<contexto>: {ex.Message}", nameof(<ViewModel>))` + notificación existente/nueva.

## Entrega y riesgos

- Estrategia: `ask-on-risk` → cadena `stacked-to-main` (cacheada). Forecast ~250-420 líneas autoradas; probablemente un solo slice de PR.
- RDD: off (clone-local) → verificación = self-verification del writer + verificador independiente por unidad + suite/cobertura en el cierre (T3).
- Riesgo principal: los tests existentes del preview construyen el `ISalesService` mock — se ajustan para proveer el nuevo método (passthrough) y se reportan nombradamente; la semántica del completar venta no se toca.
