# Remediación de Críticos de Auditoría Integral (SEC-01/02, SRE-01/02, CLEAN-02)

Objetivo: remediar los **5 hallazgos críticos verificados** de la auditoría `docs/auditoria_integral_fases_1_4.txt` (los otros 15 confirmados quedan como roadmap en changes posteriores): redondeo server-authoritative (SEC-01), guard de precio de adelantos (SEC-02), orden determinista de deducción de stock (SRE-01), idempotencia en items/caja/cierre + cancel (SRE-02) y resiliencia del commit de cantidades del carrito (CLEAN-02). Branch: V0.15. Entrega: ask-on-risk → cadena stacked-to-main (cacheada). SDD: `openspec/changes/2026-10-05-audit-critical-remediation/`. RDD: off (clone-local). Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · `npm test` (Web.Frontend) · build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: auditoría externa verificada (L1/L2) + evidencia de diseño (L3).

- **S1 (SEC-01 — redondeo server-authoritative)**: el ajuste de redondeo se recalcula en backend contra los pagos persistidos al completar la venta y se ignora el valor del cliente. Fórmula espejo del preview: `remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS − sale.TotalBsS) : 0m`; caso parcial → 0. Se eliminan el guard ±1000 y las asignaciones del valor del cliente.
- **S2 (SEC-02 — guard de adelantos)**: `IsCashAdvance` se rechaza en `AddItemAsync` y `UpdateSaleItemsAsync` con el mensaje exacto: "Los productos de adelanto de efectivo no pueden agregarse ni modificarse en una venta; use el flujo de adelanto de efectivo." El flujo legítimo (`CreateCashAdvanceSaleAsync` vía CashAdvanceCoordinator) queda intacto.
- **S3 (SRE-01 — orden determinista)**: `UpdateStockBatchAsync` resuelve cada request a su target (parent/conversión), consolida por `(target, reason, saleId)` sumando cantidades y ejecuta ordenado ascendente por ProductId; totales por target idénticos al comportamiento legado para cualquier orden de entrada.
- **S4 (SRE-02 — idempotencia)**: `Idempotency-Key` obligatorio en POST items / transaction / closure (replay con `X-Cache-Lookup: HIT`; 422 con payload distinto); índice único de `ClosureDate` + guard transaccional ("Ya existe un cierre para esta fecha."); cancel idempotente (ya anulada → éxito, no 409); clientes WPF y Web envían claves.
- **S5 (CLEAN-02 — resiliencia del carrito)**: `CommitItemQuantityAsync`/`FlushAllQuantitiesAsync` loguean (`ClientStateLogger.LogError`), restauran cantidades desde el servidor y notifican al operador (`IDialogService`) con mensajes exactos: "No se pudo actualizar la cantidad. Se restauró el valor del servidor." / "No se pudo actualizar la cantidad y no se pudo restaurar el estado. Verifique el carrito antes de cobrar."; sin catches vacíos en `CartViewModel`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S3 | delegated (writer) | Orden determinista + consolidación en deducción de stock (helper puro + tests) | pendiente |
| T2 | S1 | delegated (writer) | Redondeo server-authoritative en `CompleteSaleAsync` (+tests) | pendiente |
| T3 | S2 | delegated (writer) | Guard de adelantos en `AddItemAsync`/`UpdateSaleItemsAsync` (+tests) | pendiente |
| T4 | S4 | delegated (writer) | Idempotencia backend: resolver compartido + 3 endpoints + cancel + índice único `ClosureDate` (+tests) | pendiente |
| T5 | S4 | delegated (writer) | Clientes WPF + Web envían `Idempotency-Key` en items/transaction/closure (+tests) | pendiente |
| T6 | S5 | delegated (writer) | `CartViewModel`: logging + rollback/re-sync + notificación (+tests) | pendiente |
| T7 | S1-S5 | delegated (verify) + inline | Verificación final + ANEXO 8.149 + cierre del tracker | pendiente |

Estrategia de entrega: `ask-on-risk` → cadena **stacked-to-main** cacheada (misma política de la cadena V0.15).

## Log

- L1 (2026-10-05) — Pedido del usuario, verbatim: "Haz un verificacion de hallazgos obtenidos de una auditoria que se encuentra en /Docs/auditoria_integral_fases_1_4.txt" y, tras la propuesta de plan, "Procede". Alcance autorizado: plan SDD de remediación priorizando los críticos confirmados.
- L2 (2026-10-05) — Verificación independiente de la auditoría (4 verificadores read-only + spot-check del orquestador, evidencia estática): 20 hallazgos reales (el encabezado de la auditoría dice 19; sus cuentas 4/7/8 no cierran — el cuerpo lista 6 críticos/10 altos/4 medios). Resultado: **15 CONFIRMADOS, 1 CONFIRMADO-CON-DRIFT (CLEAN-02), 4 PARCIALES (SEC-04, PERF-03, PERF-04, CLEAN-05), 0 FALSOS**. La matriz `[x]` de la auditoría es **falsa 5/5**: SEC-01, SEC-02, SRE-01, SRE-02 y CLEAN-02 están marcados como corregidos y ninguna corrección existe en el código (verificado). Matices clave: el skip de stock de adelantos en checkout es spec-mandated (`api-dto-boundary` REQ-ADB-11); SEC-06 requiere cambio de clientes; PERF-02 choca con el retry xmin deliberado (`supplier-invoice-apply:66`); CLEAN-05: React ya consume `checkout-preview` (8.5-WEB1), WPF no.
- L3 (2026-10-05) — Plan SDD creado (exploration + proposal + 4 specs + design + tasks + state) y doc ODD. Evidencia de diseño clave: fórmula del preview `SalesController.Checkout.cs:63`; valor del cliente persistido en `SalesService.Checkout.cs:112` y `:282` (pagos se adjuntan en `:190`); guard PUT exime adelantos en `HoldOrders.cs:338-341`; `UpdateStockBatchAsync` conserva orden del caller (`StockDeduction.cs:19-34`); helper reutilizable `ResolveIdempotencyAsync` (`SalesController.cs:196-241`) + tabla `IdempotentRequests`; cierre sin guard y con índice no-único (`SalesDbContext.cs:265`); defecto del carrito en `CartViewModel.cs:388-391`/`:409-412` sin rollback ni logging (`ClientStateLogger.LogError` disponible; `_dialogService` ya inyectado). Próximo: T1 (SRE-01) delegado a writer con verificación independiente.
