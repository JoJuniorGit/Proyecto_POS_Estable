# Paso 14 — Rentabilidad y comisiones de medios de pago

Rama: `V0.15`. Entrega: work units locales; push/PR a decisión del mantenedor.
Runner: `dotnet build CommandCenter.slnx -c Release` + `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release` con `TEST_POSTGRES_CONNECTION`.
Estrategia de entrega: `ask-on-risk`. Sin push/PR sin autorización.
TDD: RED observado antes de cada implementación; GREEN; refactor con tests verdes.

## Specs

### S1 — Costos históricos desconocidos
`UnitCostUSD IS NULL` significa costo desconocido. No se inventa ni se backfillea con el costo actual del producto.
El resumen del período expone: `CostCoveragePercent`, `KnownCostRevenue`, `KnownCogs`, `UnknownCostRevenue` y `UnknownCostLinesCount`.
`GrossMarginPercent` se calcula únicamente sobre ventas con costo conocido.

### S2 — Avance de efectivo vs. mercancía
El capital dispensado en un Avance de Efectivo es movimiento de tesorería, no venta de mercancía.
Se persiste el discriminador `SaleType`/`IsService` y el monto de la comisión (`CommissionAmountUSD`/`CommissionAmountLocal`).
En el Estado de Resultados se excluye el capital del avance de `Ventas Netas` y de `CMV`, y se suma únicamente la comisión en `ServiceCommissions`.
Los avances históricos se identifican por patrón/origen para no distorsionar las ventas de mercancía.

### S3 — Timestamp de anulación
Se agrega `CancelledAtUtc` nullable a `Sale` con índice.
En la migración se backfillean las ventas ya anuladas usando su fecha de actualización/emisión para mantener consistencia histórica.

### S4 — Snapshot de costo multi-régimen
Cada `SaleItem` conserva el snapshot de costo en moneda base (`UnitCostUSD`) y en la moneda del régimen/transacción (`UnitCostLocal` o derivado inmutable con `AppliedRate`).
`Single` (COP, VES, EUR, MXN) y `Dual` calculan el CMV sin consultar tasas ni costos fuera de la venta.

### S5 — CMV y utilidad percibida
Prorrateo por factor de costo/margen de la factura: por cada cobro efectivamente recibido en el período (venta de contado, inicial o abono de crédito `ReceivablePayment`), el CMV percibido es `MontoCobrado * (CMV_Neto_Factura / Total_Neto_Factura)`.
La utilidad bruta percibida es `MontoCobrado - CMV_Percibido` más el `FxDifferenceBsS` realizado y menos los gastos activos de Caja Chica del período.

### S6 — Índices, exportación y gráficos
Índice idempotente en `CreditReturn(CreatedAtUtc)`.
F3: exportación PDF, Excel (`.xlsx`) y CSV en el Backend, reutilizable desde WPF y Web.
F4: gráficos comparativos con primitivas nativas (SVG/CSS en Web y controles nativos en WPF), sin paquetes externos de terceros.

### S7 — Comisiones por método de pago
La comisión del método de pago es un costo del negocio y solo se usa para calcular beneficios. Nunca se carga al cliente.
Configuración `PaymentMethodFeeRule` con vigencia (`EffectiveFromUtc`/`EffectiveToUtc`), `FeeType` (`Percentage`, `Fixed`, `PercentagePlusFixed`), `Percentage`, `FixedAmount` y `CurrencyCode` (USD o moneda local del régimen).
Snapshot en `SalePayment` y `ReceivablePayment`: `PaymentMethodFeeRuleId`, `FeeType`, `FeePercentage`, `FeeFixedAmount`, `FeeCurrencyCode`, `FeeAmountUsd`, `FeeAmountLocal`, `FeeRate`, `FeeConfigured`, `FeeRuleEffectiveFromUtc`.
`FeeAmountUsd` es `decimal(18,4)`; `FeeAmountLocal` es `decimal(18,2)`.
La conversión usa la tasa snapshot del pago. Sin regla activa: `FeeConfigured=false`, montos 0.
En Avance de Efectivo la comisión del medio se calcula sobre el total cobrado (capital + comisión de servicio); el capital no es ingreso.
Sin backfill de comisiones históricas; se reporta cobertura (`FeeCoveragePercent`, `UnknownFeePaymentsCount`, `UnknownFeePaymentAmount`).

### S8 — Fórmula corregida de utilidad percibida
```text
RealizedGrossProfit = KnownCostCollectedRevenue - RealizedCogs
RealizedGrossMarginPercent = RealizedGrossProfit / KnownCostCollectedRevenue * 100
```
`UnknownCostCollectedRevenue` se mantiene segregado fuera del margen realizado. No se resta `RealizedCogs` del total de `CobrosMercancia` porque ese total incluye facturas sin costo completo y se asumiría un margen falso del 100%.

### S9 — EffectiveTotalUSD y multi-régimen
En las agregaciones de ventas en USD se usa `Sale.EffectiveTotalUSD` con fallback a `TotalUSD` solo en registros legacy que no lo tengan.
El motor soporta `Dual` y `Single`. En `Single` (moneda única no-USD) los KPIs se expresan en la `PrimaryCurrencyCode` del comercio usando los snapshots locales (`TotalBsS`/local, `UnitCostLocal`, `FeeAmountLocal`, `CommissionAmountLocal`), sin obligar a leer el Estado de Resultados en USD.

### S10 — Reversas de comisión del período
Cuando una venta se anula (`Sale.CancelledAtUtc ∈ [from, to)`), sus `SalePayments` originales tenían comisión registrada. La resta `- reversas del período` en `PaymentMethodFees` descuenta exactamente los `FeeAmountUsd`/`FeeAmountLocal` de los `SalePayments` pertenecientes a ventas cuyo `CancelledAtUtc` cae en el período consultado.
En devoluciones de mercancía donde no se reversa el cobro electrónico inicial, no se revierte falsamente la comisión del adquirente.

### S11 — Ubicación en UI Settings
En F4, la gestión de `PaymentMethodFeeRule` vive dentro del apartado dedicado de **Métodos de Pago y Caja** en Configuración, solo `Admin`.

### S12 — Secuencia F1 y evidencia
F1 se ejecuta en orden `F1-WU1` → `F1-WU6`. Al cerrar se presenta: build 0/0, suite completa, migración aplicada dos veces, drift tests, conteos de backfill, tests de snapshot de comisión y commits.

## Tasks

- F1-WU1 | S3/S6/S7/S12 | Migración idempotente, tablas/columnas, `ModelSnapshot`, drift tests | delegated | completed (commit afe8ebe)
- F1-WU2 | S2/S3/S10 | Service wiring de `SaleType`/comisión/`CancelledAtUtc`, corrección de `SaleId` en `CashTransactions` y backfill correctivo de comisión | delegated | completed (commit 59ba573)
- F1-WU3 | S4/S9 | `UnitCostLocal`, helper de snapshot Single/Dual | delegated | completed (commit 33b3aaf)
- F1-WU4 | S7/S10 | `PaymentMethodFeeRule`, snapshots, resolver y wiring en pagos | delegated | completed (commit d31f9c0)
- F1-WU5 | S6 | Índice `CreditReturn.CreatedAtUtc` e integración PostgreSQL | delegated | completed (commit a49765a)
- F1-WU6 | S1-S12 | Verificación integral F1 y evidencia | delegated | completed (commit 837a898)

## F2 Specs

### F2-S1 — Ventanas UTC por Zona Horaria Global
Los cortes `Hoy`, `Ayer`, `Semana`, `Mes` y el rango `[FromDate, ToDate]` se interpretan en la Zona Horaria Global configurada en Settings y se convierten a límites UTC estrictos `[fromUtc, toUtc)`.
Todas las consultas usan comparaciones directas `>= fromUtc` y `< toUtc` sobre `Date`, `CancelledAtUtc`, `CreatedAt`, `ReceivedAtUtc` y `CreatedAtUtc`, sin funciones SQL sobre columnas indexadas.

### F2-S2 — Bloque Devengado (`Accrued`)
`VentasBrutasMercancia` suma `Sale.EffectiveTotalUSD` (fallback a `TotalUSD` si `0` en legacy) en `Dual`, o el total local en `Single`, donde `SaleType == Merchandise`, `Sale.Date ∈ [fromUtc, toUtc)` y `(CancelledAtUtc IS NULL OR CancelledAtUtc >= fromUtc)`.
`Anulaciones` y `Devoluciones` restan en el período del evento (`CancelledAtUtc ∈ [fromUtc, toUtc)` y `CreditReturn.CreatedAtUtc ∈ [fromUtc, toUtc)`).
`GrossProfit = KnownCostRevenue - KnownCogs`, excluyendo `UnknownCostRevenue` del margen bruto.
`PaymentMethodFees` suma comisiones de `SalePayment` y `ReceivablePayment` del período menos las reversas de comisión de los `SalePayment` cuyas ventas tengan `CancelledAtUtc ∈ [fromUtc, toUtc)`.
`NetProfitAccrued = GrossProfit + ServiceCommissions - PettyCashExpenses - PaymentMethodFees`; `FxDifference` realizado se expone como renglón informativo, sin sumarlo al neto devengado.

### F2-S3 — Bloque Percibido en Caja (`Realized`)
`RealizedCogs` se prorratea por factura de mercancía con cobertura de costo del 100%: `CobroImputado * (CMV_Neto_Factura / Total_Neto_Factura)`, usando `CreditPaymentAllocation.AmountUSD` (o equivalente local en `Single`) para abonos CxC y descontando `CreditReturn.RefundedUSD`.
`RealizedGrossProfit = KnownCostCollectedRevenue - RealizedCogs`; `UnknownCostCollectedRevenue` queda separado fuera del margen.
`NetProfitRealized = RealizedGrossProfit + RealizedServiceCommissions + RealizedFxDifference - RealizedPettyCashExpenses - RealizedPaymentMethodFees`.

### F2-S4 — Desgloses, Filtros y Multi-Régimen
Serie temporal agrupada por hora, día, semana o mes.
Desglose por método de pago con comisiones de adquirente y `FeeCoveragePercent`.
Ranking de productos y categorías por contribución monetaria y margen.
Alertas de erosión de margen.
Filtros combinables por rango de fecha, categoría (incluyendo subárbol), vendedor/cajero y canal/tipo.
Al filtrar por categoría o producto, el DTO aclara que `PettyCashExpenses`, `ServiceCommissions` y `PaymentMethodFees` son gastos/ingresos globales de operación y no se prorratean artificialmente por artículo.
En `Single` los KPIs se expresan en la `PrimaryCurrencyCode` usando snapshots locales; en `Dual` la cabeza es USD con equivalentes locales cuando aplique.

### F2-S5 — Integración PostgreSQL, Batch Fee Lookup y Evidencia
La suite de integración de F2 corre sobre PostgreSQL real e inserta `PaymentMethodFeeRule`, `SalePayment`, `ReceivablePayment`, un avance de efectivo y una anulación para certificar FKs, precisión `18,4`/`18,2` y lectura agregada desde `ProfitabilityService`.
El `PaymentMethodFeeResolver` incorpora resolución en bloque por conjunto de `PaymentMethodId` para checkout multipago y lote de abonos, eliminando el lookup N+1 por línea.
F2-WU5 incluye `EXPLAIN ANALYZE` de consultas clave y regresión completa.

## F2 Tasks

- F2-WU1 | F2-S1/F2-S5 | DTOs, filtros, helper de ventana UTC, batch fee resolver y tests | delegated | completed (commit 323b15b)
- F2-WU2 | F2-S2 | Motor devengado, cobertura de costos/comisiones y reversas por anulación | delegated | completed (commit 631713d)
- F2-WU3 | F2-S3 | Motor percibido con prorrateo de CMV, asignaciones CxC, FX y reembolsos | delegated | completed (commit 1340e34)
- F2-WU4 | F2-S4 | Series, desgloses, filtros de categoría/producto/vendedor, alertas y Multi-Régimen | delegated | completed (commit 6dd3c02)
- F2-WU5 | F2-S5 | Integración PostgreSQL real, fee inserts, EXPLAIN ANALYZE y regresión completa | delegated | completed (commit fa045cc)

## F3 Specs

### F3-S1 — API REST y RBAC
`ProfitabilityController` (`/api/profitability`):
- `GET /api/profitability/summary` devuelve `ProfitabilitySummaryDto`.
- `GET /api/profitability/breakdown` devuelve `ProfitabilityBreakdownDto` (series, métodos, productos/categorías, vendedores y alertas).
- Autorización `RequireAdminOrManager` (`Admin`, `Manager`); `Cashier` y no autenticados reciben 403/401.
- Mapeo de filtros período/fechas, categorías con subárbol, cajero y tipo/canal; propagación de `HttpContext.RequestAborted`.
`PaymentMethodFeesController` (`/api/payment-method-fees`):
- CRUD de `PaymentMethodFeeRule` exclusivo `RequireAdmin`.
- Validación de vigencias coherentes, sin solapamientos para el mismo método, porcentaje `(0, 100]`, fijo `>= 0`, moneda compatible con el régimen y método no efectivo.
- Baja lógica para preservar snapshots históricos.

### F3-S2 — Exportación CSV y XLSX
CSV: UTF-8 con BOM, delimitador `;`, escape de fórmulas `=`, `+`, `-`, `@` y formato numérico por moneda.
XLSX con ClosedXML: pestañas `Resumen Ejecutivo`, `Por Producto y Categoría`, `Medios de Pago` y `Caja Chica por Categoría` (detalle aprobado); cabeceras estilizadas, moneda/porcentaje y columnas autoajustadas.
La exportación incluye metadatos de corte local, usuario y filtros aplicados.

### F3-S3 — PDF 1.4 Nativo
`ProfitabilityPdfGenerator` reutiliza las primitivas vectoriales nativas del proyecto, sin librerías externas.
Incluye membrete comercial, metadatos de emisión/zona horaria, bloques comparativos Devengado vs. Percibido, tabla de Estado de Resultados, detalle de Caja Chica por categoría y pie `Página X de Y`.
Endpoint unificado `GET /api/profitability/export?format=csv|xlsx|pdf`; retorna `File` con `Content-Type` y nombre `Estado_Resultados_[Periodo].ext`.

### F3-S4 — Integración, Seguridad y Cierre
Tests HTTP de contratos JSON, propagación de filtros y matrices RBAC (`Admin`/`Manager` lectura; `Admin` fees; `Cashier` 403; no autenticado 401).
Tests de contenido/formato CSV, XLSX (workbook abrible) y PDF (`%PDF-1.4`, `%%EOF`, longitud no vacía).
Build Release 0/0, suite completa en verde y ANEXO LXIII.

### F3-S5 — Caja Chica por Categoría en Reportes
Los reportes descargables incluyen el desglose de gastos de Caja Chica por categoría, cantidad y moneda de reporte como sección/pestaña de detalle. El KPI principal `PettyCashExpenses` sigue siendo global y no se prorratea por artículo.

## F3 Tasks

- F3-WU1 | F3-S1 | `ProfitabilityController`, `PaymentMethodFeesController`, `PaymentMethodFeeRuleService` y RBAC | delegated | completed (commit b238146)
- F3-WU2 | F3-S2/F3-S5 | Exportación CSV y XLSX con detalle de Caja Chica | delegated | pending
- F3-WU3 | F3-S3/F3-S5 | Generador PDF 1.4 nativo y endpoint unificado | delegated | pending
- F3-WU4 | F3-S4 | Integración HTTP/RBAC, contenido de exports y cierre ANEXO LXIII | delegated | pending

## Log

- L1 (2026-11-09): pedido verbatim del mantenedor:

  > Los 10 puntos de diseño quedan aprobados, junto con la secuencia F1-WU1 → F1-WU6. Antes de ejecutar F1-WU1, incorpora 4 ajustes de precisión contable detectados en las fórmulas de 3.2, 4.2 y 4.3 del plan:
  >
  > 1. **Corrección en la fórmula de RealizedGrossProfit (§4.3):**
  >    En el borrador de §4.3 pusiste:
  >    `RealizedGrossProfit = CobrosMercancia - RealizedCogs`
  >    Pero CobrosMercancia incluye cobros de facturas con costo desconocido (UnknownCostCollectedRevenue), mientras que RealizedCogs solo suma facturas con cobertura del 100%. Si restas ambos directamente, todo cobro de una factura sin costo se sumaría como 100% ganancia en la vista percibida.
  >    Fórmula corregida (simétrica con §4.2):
  >    `RealizedGrossProfit = KnownCostCollectedRevenue - RealizedCogs`
  >    `RealizedGrossMarginPercent = RealizedGrossProfit / KnownCostCollectedRevenue * 100`
  >    Manteniendo UnknownCostCollectedRevenue segregado fuera del margen realizado.
  >
  > 2. **Precisión de FeeAmountUsd a 4dp (decimal(18,4)) en §3.2:**
  >    Define FeeAmountUsd como decimal(18,4) (alineado con el estándar interno de 4dp para USD en el resto del sistema) y mantén FeeAmountLocal en decimal(18,2).
  >    En pagos pequeños (ej. una venta de $1.00 con comisión de 1.5% = $0.0150), truncar o redondear FeeAmountUsd a 2dp en cada pago individual introduce un sesgo de hasta 33% al sumar miles de transacciones.
  >
  > 3. **Uso de EffectiveTotalUSD y Soporte Multi-Régimen (Single vs. Dual) en §4.2 y §4.3:**
  >    Donde las fórmulas de §4.2 y §4.3 mencionan Sale.TotalUSD, utiliza Sale.EffectiveTotalUSD (con fallback a TotalUSD solo en registros legacy que no lo tengan), para respetar la invariante de redondeo quirúrgico.
  >    Asegúrate de que en régimen Single (moneda única no-USD, como COP, VES, MXN, EUR) los KPIs se expresen en la PrimaryCurrencyCode del comercio utilizando los snapshots locales (TotalBsS/local, UnitCostLocal, FeeAmountLocal), sin obligar a un negocio monomonetario en pesos o bolívares a leer su Estado de Resultados en USD.
  >
  > 4. **Definición explícita de "Reversas de Comisión del Período" (§4.2 y §4.3):**
  >    Cuando una venta se anula (Sale.CancelledAtUtc ∈ [from, to)), sus SalePayments originales tenían comisión registrada. Asegúrate de que la resta de - reversas del período en PaymentMethodFees descuente exactamente los FeeAmount de los SalePayments pertenecientes a ventas cuyo CancelledAtUtc caiga en el período consultado (mientras que en devoluciones de mercancía donde no se reversa el cobro electrónico inicial, no se revierta falsamente la comisión del adquirente).
  >
  > 5. **Ubicación en UI Settings (§6):**
  >    En F4, ubica la gestión de PaymentMethodFeeRule dentro del apartado dedicado de **Métodos de Pago y Caja** en Configuración (solo Admin).
  >
  > Ejecuta la Fase 1 en orden (F1-WU1 -> F1-WU6), presenta toda la evidencia comprometida al cerrar.

- L2 (2026-11-09): aprobación de los 10 puntos de diseño. Ajustes incorporados a S7 (FeeAmountUsd 18,4), S8 (KnownCostCollectedRevenue), S9 (EffectiveTotalUSD + Single/Dual) y S10 (reversas por anulación). Inicio de F1.

- L3 (2026-11-09): **F1-WU1 COMPLETADA**. Se agregó el esquema base: `SaleType`, `CommissionAmountUSD` (18,4), `CommissionAmountLocal` (18,2), `CancelledAtUtc` + índice, `SaleItem.UnitCostLocal` (18,4), `PaymentMethodFeeRule` con vigencia e índice único, snapshots de comisión en `SalePayment`/`ReceivablePayment` (`FeeAmountUsd` 18,4, `FeeAmountLocal` 18,2), índice `CreditReturn.CreatedAtUtc` e índices de soporte. Migración idempotente `20261109231727_AddProfitabilityFoundationRev8213` con backfills iniciales y `ModelSnapshot` regenerado. Evidencia: RED 8/8 fallos del contrato de esquema; GREEN 9/9 (se agregó guarda de precisión con SQLite relacional); `MigrationIndexDriftTests` + `PendingModelChangesTests` verdes (12/12 filtrados); build Release 0/0. Verificación independiente: pass-with-findings. Hallazgo alto: el flujo de avance crea `CashTransactions` sin `SaleId`, por lo que el backfill de comisión histórica es no-op; se corrige en WU2 pasando `referenceId` y con backfill correctivo. Hallazgos bajos: defaults de `PaymentMethodFeeRule` solo en la migración; guardas de FK por nombre; la guarda de precisión ya quedó cubierta por test. WU1 incluye los backfills iniciales; WU2 agrega el wiring de servicio y el backfill correctivo.

- L4 (2026-11-09): **F1-WU2 COMPLETADA**. El avance persiste `SaleType.CashAdvance`, `CommissionAmountLocal` y `CommissionAmountUSD` 4dp; en el camino method-aware la comisión USD usa `receivedCommissionUsd` (corrige el 5 → 0.125 detectado por el verificador). La línea sintética guarda `UnitCostLocal = 0`. Los dos `AddTransactionAsync` del avance (legacy y method-aware) pasan `referenceId: createdSale?.Id`, de modo que `CashTransactions.SaleId` queda ligado. Los tres sitios de anulación setean `CancelledAtUtc = DateTime.UtcNow` (venta en espera, anulación de crédito sin pagos y devolución total sin dinero recibido). Migración data-only `20261110000007_CorrectCashAdvanceCommissionBackfillRev8213` con guardas de sesión/moneda y reconciliación capital+comisión = total del pago; los avances con cruce de moneda o alias legacy quedan en cero y se reportan como cobertura/desconocido, nunca se adivinan. Evidencia: RED 3/3 + USD 5 vs 0.125; GREEN 70/70 focalizados + 4/4 drift; build Release 0/0. Verificación independiente final: pass-with-findings; único hallazgo bajo: cobertura del backfill para avances cross-currency/alias legacy, diferido a WU6.

- L5 (2026-11-09): **F1-WU3 COMPLETADA (work unit local, sin commit)**. `SaleItem.UnitCostLocal` se materializa al completar la venta en ambos regímenes mediante `ApplyLocalCostSnapshots(sale)` en el bloque final de `SalesService.Checkout.cs`: `UnitCostUSD * sale.AppliedRate` redondeado a 4dp `AwayFromZero`, o `null` si el costo es desconocido. Solo usa el snapshot inmutable de la venta (no lee costo vigente del catálogo ni tasas externas); las ediciones pendientes/en espera no lo escriben y el avance de efectivo conserva su `0m` explícito (crea su venta por la ruta directa, sin pasar por el helper). Evidencia: RED 2 fallos observados (`100.0000` vs `null` en Dual USD/VES tasa 40; `2.50` vs `null` en Single COP identidad) + guarda de costo desconocido; GREEN 6/6 focalizados, 57/57 filtro autorizado (SaleItemCostSnapshot, DualSecondaryCurrencyCheckout, SaleMonetarySnapshot, SaleAmountSnapshot, CheckoutAndPayment, CashAdvanceProfitabilitySnapshot), 4/4 drift (`MigrationIndexDriftTests` + `PendingModelChangesTests`), build Release 0/0. Sin migraciones aplicadas y sin suite completa.

- L6 (2026-11-09): **F1-WU3 cierre de hallazgos del verificador**. Medio: el camino de hold con pago inicial del 100% completa la venta sin pasar por `CompleteSaleCoreAsync`; ahora `HoldSaleAsync` invoca `ApplyLocalCostSnapshots(sale)` en el bloque común (intento inicial y retry), después de fijar los snapshots monetarios y antes de `ClearHoldClaim`/`SaveChangesAsync`. RED observado: `HoldSaleAsync_WithFullInitialPayment_CompletesAndPublishesEvent` con `UnitCostUSD = 20m` a tasa 50 esperaba `UnitCostLocal = 1000`, obtuvo `null`; GREEN tras el fix. Bajo: triangulación de redondeo 4dp con par manual `USD/COP` a `40.1235` (`UnitCostUSD = 2.50` → `UnitCostLocal = 100.3088`, desde 100.30875) y pago 5 USD / 200.62 COP conciliado con el total local. Evidencia: build Release 0/0; filtro autorizado exacto 86 superadas + 1 fallo ambiental (`HoldOrderClaimTestsPostgres` requiere `TEST_POSTGRES_CONNECTION`, no configurada, y aplicaría migraciones fuera de alcance); subconjunto sin Postgres 86/86; drift 4/4. Sin commit, sin migraciones aplicadas y sin suite completa.

- L7 (2026-11-09): **F1-WU3 re-verificada y cerrada**. La verificación independiente final dio pass-with-findings; el único hallazgo bajo era que el test de redondeo no distinguía `AwayFromZero` de `ToEven`. Se ajustó a `2.53 x 40.1235 = 101.512455 -> 101.5125` (ToEven daría `101.5124`). Con `TEST_POSTGRES_CONNECTION` configurada, el filtro autorizado exacto pasó **87/87, 0 skipped**, incluido `HoldOrderClaimTestsPostgres`; el carril PostgreSQL creó, migró y eliminó una base descartable, validando por primera vez las migraciones WU1/WU2 en PostgreSQL. Drift 4/4. Build Release 0/0. WU3 puede cerrar.

- L8 (2026-11-09): **F1-WU4 COMPLETADA (work unit local, sin commit)**. `PaymentMethodFeeResolver` resuelve la regla vigente (`IsActive`, `EffectiveFromUtc <= fecha`, `EffectiveToUtc == null || > fecha`), rechaza solapamientos (más de una regla activa) y monedas distintas de USD/local, y congela `FeeAmountUsd` 4dp / `FeeAmountLocal` 2dp con la tasa snapshot del pago. Los métodos efectivo devuelven `None` aunque exista una regla. `ResolveLocalCurrencyCode` devuelve la secundaria en Dual (fallback primaria) y la primaria en Single; se agregó un overload para el snapshot interno del servicio. El snapshot se aplica antes de persistir en checkout, abono inicial de hold, add-payment simple y batch, avance de efectivo (sobre el total cobrado capital + comisión de servicio) y abono CxC; el clon del retry de hold copia los 10 campos sin re-resolver. Los cobros con método inexistente en catálogos legacy vacíos mantienen el comportamiento previo (snapshot `None`), mientras el resolver directo falla cerrado con mensaje de configuración. Evidencia: RED observado (fallos de compilación por API inexistente); GREEN 15/15 `PaymentMethodFeeResolverTests` + 7/7 `PaymentMethodFeeSnapshotTests`; filtro autorizado sin Postgres 111/111; drift 4/4; build Release 0/0. Sin migraciones aplicadas, sin suite completa y sin commit.

- L9 (2026-11-09): **F1-WU4 re-verificada y cerrada**. Se corrigieron los hallazgos bajos del verificador: `PaymentMethodFeeResolver` ahora exige porcentaje en `(0, 100]` y rechaza fijo negativo en `PercentagePlusFixed`; el abono CxC usa `snapshotPaymentRate` (4dp persistido) para la comisión, no la tasa de cálculo 8dp. Tests nuevos: >100%, fijo negativo, midpoint `1.225% -> 0.0123` (distingue `AwayFromZero` de `ToEven`) y vigencia inclusiva. Verificación final independiente: pass; filtro autorizado 115/115 + drift 4/4; build Release 0/0. N+1 del lookup de método diferido a F2; persistencia de fees solo InMemory, PostgreSQL en WU5/WU6; reversas S10 en WU6.

- L10 (2026-11-09): **F1-WU5 COMPLETADA (work unit local, sin commit)**. Nuevo smoke PostgreSQL real `ProfitabilityFoundationMigrationSmokeTests` sobre el patrón del carril aislado: migra hasta `20261107020155_AddCashDrawerSessionMethodBalancesRev8212`, siembra por SQL crudo una venta de mercancía con costo, un avance con pago `ADELANTO-…` y su pareja egreso físico/ingreso no físico dentro de 10 minutos, una venta anulada y una sesión de caja con dos movimientos; migra al último nivel y verifica esquema, índices, FKs y backfills. Evidencia: RED inicial del propio contrato de columnas de fee (PostgreSQL sintetiza precision 32/escala 0 para `integer`; 8/10 columnas coincidían) corregido en la aserción; GREEN 1/1 del filtro `ProfitabilityFoundationMigrationSmokeTests`. El test comprueba `Sales.SaleType` integer NOT NULL default 0, comisiones 18,4/18,2, `CancelledAtUtc` timestamptz nullable, `SaleItems.UnitCostLocal` 18,4 nullable, las 10 columnas de fee en `SalePayments`/`ReceivablePayments`, `PaymentMethodFeeRules` con índice único, 3 FKs y sin columna física `xmin`, e índices `IX_CreditReturns_CreatedAtUtc`/`IX_Sales_CancelledAtUtc`/`IX_Sales_SaleType_Status_Date`; backfill: avance `SaleType=1`/comisión 70,00 local y 1,4 USD, mercancía `SaleType=0` y `UnitCostLocal=100,0000`, anulada `CancelledAtUtc = Date`; segunda aplicación de `MigrateAsync()` sin error e historial con ambas migraciones exactamente una vez. Drift 4/4. Build Release 0/0. Sin cambios de comportamiento productivo, sin migraciones aplicadas a la base real y sin commit.

- L11 (2026-11-09): **F1-WU5 re-ejecutada y cerrada por el padre**. Con `TEST_POSTGRES_CONNECTION` local: build Release 0/0; `ProfitabilityFoundationMigrationSmokeTests` 1/1; drift 4/4. La prueba crea una base descartable, migra al nivel previo, siembra datos legacy, aplica WU1+WU2, verifica esquema/índices/FKs/backfills y vuelve a aplicar `MigrateAsync()` sin duplicar historial. Es un work unit test-only: no cambia comportamiento productivo ni migraciones.

- L12 (2026-11-09): **F1-WU6 COMPLETADA**. Verificación integral: build Release 0/0; suite completa `CommandCenter.Tests` con `TEST_POSTGRES_CONNECTION` **4262/4262, 0 skipped, 4m52s**; smoke PostgreSQL 1/1; drift 4/4. Se corrigió un mock de `CashDrawerCashAdvanceSecurityTests` que no incluía el nuevo parámetro opcional `receivedCommissionUsd` de `ISalesService.CreateCashAdvanceSaleAsync` (7/7 tras el fix). Backfill verificado: avance `SaleType=1`/comisión 70,00 local y 1,4000 USD; mercancía `UnitCostLocal=100,0000`; anulada `CancelledAtUtc=Date`; migración aplicada dos veces sin duplicar historial. ANEXO LXI registrado. Sin cambios de comportamiento productivo adicionales.

- L13 (2026-11-09): pedido verbatim del mantenedor (F2):

  > F1 queda aprobada con los 4262/4262 tests en verde y la verificación de migración e idempotencia en PostgreSQL aislado. No hace falta pausar para revisar el diff: **arranca directamente con F2 (`ProfitabilityService`) bajo TDD (RED -> GREEN)**.
  >
  > 1. **Integración Real en PostgreSQL para Comisiones (`PaymentMethodFeeRule` + Pagos):** Incluye en la suite de integración de F2 sobre PostgreSQL real la inserción completa de reglas de comisión, `SalePayment` y `ReceivablePayment` (incluyendo un avance de efectivo y una anulación) para certificar tanto las FKs/precisión `18,4`/`18,2` como la lectura agregada desde `ProfitabilityService`.
  > 2. **Eliminación del Lookup N+1 en `PaymentMethodFeeResolver`:** Agrega resolución en bloque (batch lookup por conjunto de `PaymentMethodId` para el timestamp del cobro) en checkout multipago y lote de abonos para evitar consultas N+1 por cada línea de pago.
  >
  > Invariantes obligatorias: ventanas temporales por Zona Horaria Global a límites UTC `[fromUtc, toUtc)`; bloque `Accrued` con `VentasBrutasMercancia` sobre `Sale.EffectiveTotalUSD` (fallback `TotalUSD` legacy) o total local en `Single`, `Anulaciones`/`Devoluciones` en el período del evento, `GrossProfit = KnownCostRevenue - KnownCogs` excluyendo `UnknownCostRevenue`, `PaymentMethodFees` menos reversas de `SalePayment` de ventas anuladas en el período, y `NetProfitAccrued = GrossProfit + ServiceCommissions - PettyCashExpenses - PaymentMethodFees` con `FxDifference` realizado informativo; bloque `Realized` con `RealizedCogs = CobroImputado * (CMV_Neto_Factura / Total_Neto_Factura)` sobre facturas con cobertura 100%, `RealizedGrossProfit = KnownCostCollectedRevenue - RealizedCogs`, `UnknownCostCollectedRevenue` separado y `NetProfitRealized = RealizedGrossProfit + RealizedServiceCommissions + RealizedFxDifference - RealizedPettyCashExpenses - RealizedPaymentMethodFees`; desgloses por serie temporal, método de pago con `FeeCoveragePercent`, ranking de productos/categorías, alertas de erosión de margen, filtros por fecha/categoría con subárbol/vendedor/canal y nota de costos globales no prorrateados; soporte `Single` y `Dual`.
  >
  > Secuencia: **F2-WU1** DTOs/filtros/ventana UTC/batch fee resolver; **F2-WU2** motor Devengado + cobertura + reversas; **F2-WU3** motor Percibido con prorrateo, CxC, FX y reembolsos; **F2-WU4** series, desgloses, filtros, alertas y Multi-Régimen; **F2-WU5** integración PostgreSQL real, fee inserts, `EXPLAIN ANALYZE` y regresión completa. Ejecuta F2 de principio a fin y presenta la evidencia completa al cerrar `F2-WU5` antes de pasar a `F3 (API, RBAC y Exportación)`.

- L14 (2026-11-09): **F2-WU1 COMPLETADA**. Se agregaron los contratos `Sales.Module/DTOs/Profitability/` (filtros, summary, bloques accrued/realized/coverage, breakdowns y alertas), `ProfitabilityTimeWindow` con presets Hoy/Ayer/Semana/Mes/Custom en la Zona Horaria Global a límites UTC `[fromUtc, toUtc)`, el servicio `IProfitabilityService`/`ProfitabilityService` (ventana + moneda de reporte Dual USD / Single primaria, nota exacta de costos globales) y el batch fee resolver de dos consultas. Checkout multipago, lote de abonos y abonos iniciales de hold cargan los contextos una sola vez; se eliminó el lookup N+1 de esos loops. Evidencia: RED por compilación faltante de DTOs/servicio; GREEN 101/101 focalizados + drift 4/4; build Release 0/0. Verificación independiente: pass-with-findings; se corrigió el `TimeZoneId` efectivo ante fallback, se agregaron tests DST y de fallback de zona inválida, y un test de conteo de consultas que prueba exactamente 2 queries del batch loader. Los motores devengado/percibido y los desgloses quedan para WU2-WU4; el timestamp del batch es único por lote.

- L15 (2026-11-09): **F2-WU2 COMPLETADA**. `ProfitabilityService.Accrued.cs` implementa el bloque devengado y la cobertura: ventas brutas de mercancía con `InvoiceNumber != null`, fecha en ventana y guarda de anulación; anulaciones en el período del evento; devoluciones por `CreditReturn.CreatedAtUtc`; `KnownCostRevenue`/`KnownCogs`/`UnknownCostRevenue` a nivel línea; `UnknownCostLinesCount` bruto; comisiones de avance; caja chica con reversas; fees de `SalePayment`/`ReceivablePayment` con reversa S10; `FxDifference` informativo; `NetProfit` y márgenes sobre `KnownCostRevenue`; cobertura de costos y fees. Se corrigieron la cancelación y la devolución en la misma ventana para que no se dupliquen, el discriminador `InvoiceNumber` para ventas emitidas, la cobertura de fees excluyendo efectivo (sin fee aplicable) y el timestamp único de devolución total. Evidencia: RED inicial 6/6, RED de cancelación 3/3, RED de cobertura/conteo 2/2; GREEN 34/34 focalizados + drift 4/4; build Release 0/0. Verificación independiente: pass-with-findings; hallazgos bajos corregidos. Quedan Realized (WU3), desgloses/filtros de categoría (WU4) y PostgreSQL/EXPLAIN (WU5).

- L16 (2026-11-10): **F2-WU3 COMPLETADA (work unit local, sin commit)**. `ProfitabilityService.Realized.cs` implementa el bloque percibido: cobros `SalePayment` y asignaciones `CreditPaymentAllocation` de facturas de mercancía con `InvoiceNumber`, reembolsos `CreditReturn.RefundedUSD/BsS` en la ventana, `RealizedCogs = CobroImputado * (CMV_Neto_Factura / Total_Neto_Factura)` sobre facturas con cobertura 100%, `RealizedGrossProfit = KnownCostCollectedRevenue - RealizedCogs`, `UnknownCostCollectedRevenue` segregado, comisiones de avance reutilizando la regla devengada, `FxDifference` por `ReceivablePayment` (una vez por cabecera y con el mismo filtro `PaymentMethodIds` que fees/asignaciones), caja chica con reversas y fees `SalePayment`/`ReceivablePayment` con reversa S10 y filtro `PaymentMethodIds`. Extensión aprobada por el mantenedor: `ProductIds` no lanza excepción; el pago se prorratea por `SelectedRevenue` original contra el monto original de la factura (antes de devoluciones), el reembolso por la porción de ingreso devuelto de los productos seleccionados y el CMV por `SelectedCogs` neto contra `NetTotal`; así un producto seleccionado totalmente devuelto netea en cero en vez de negativo. En `Single` el ingreso devuelto usa `Sale.AppliedRate` (snapshot de la factura), no `CreditReturn.RefundRate` (tasa del último pago). `ServiceCommissions`, `PettyCashExpenses` y `PaymentMethodFees` quedan globales respecto de `ProductIds`; el guard de `CategoryIds` de la fachada queda intacto para WU4. Evidencia inicial: RED 11/11 del bloque percibido en cero; GREEN 11/11. Ronda del verificador: RED 4/4 (FX con método excluido 12 vs 4; producto totalmente devuelto -100 vs 0; reembolso prorrateado 110 vs 90; devolución Single 3157,8947 vs 3000); GREEN 15/15 `ProfitabilityRealizedTests`; filtro autorizado 42/42 (Realized + Accrued + Contract + TimeWindow, incluyendo el test devengado con filtro de producto); cobertura adicional de `NetTotal <= 0` y asignación CxC Single con devolución; drift 4/4; build Release 0/0. Sin migraciones aplicadas, sin suite completa y sin commit.

- L17 (2026-11-10): **F2-WU4 COMPLETADA**. Se agregó el catálogo de inventario para filtros (`GetProductIdsByCategoryAsync` con subárbol e intersección con `ProductIds`, `GetProductCategoryInfoAsync` con primera categoría determinista), el filtro de categoría fail-closed sin catálogo, y el motor de desgloses: series por hora/día/semana/mes en la Zona Horaria Global con relleno de períodos vacíos y agregación por evento (sin prorrateo), métodos de pago con fees/cobertura y reversas S10 reconciliadas con el total, ranking de productos/categorías y vendedores, y alertas `MARGIN_EROSION` (5 pp Warning / 10 pp Critical contra el período anterior). Se corrigieron el filtro `PaymentMethodIds` en fees devengados, la reversa de fees por método, el DST de la serie horaria, la valoración de devoluciones Single con `Sale.AppliedRate`, los tie-breakers deterministas y se dividió `ProfitabilityService.Breakdown` en seis partials <=500 líneas. Evidencia: RED por compilación faltante; GREEN 18/18 nuevos, 60/60, luego 73/73 del filtro autorizado + drift 4/4; build Release 0/0. Verificación independiente: pass-with-findings; High de reconciliación corregido; Medium DST y devolución Single corregidos. Queda WU5 (PostgreSQL real, fee inserts, EXPLAIN ANALYZE, suite completa); los partials Accrued/Realized aún superan la guía de 500 líneas y el desglose por método no aplica filtros de cajero/tipo/canal a fees/FX de CxC (residual documentado).

- L18 (2026-11-10): **F2-WU5 COMPLETADA (work unit local, sin commit)**. Nuevo `CommandCenter.Tests/Integration/ProfitabilityPostgresTests.cs` sobre el carril aislado (`IsolatedPostgresDatabase.CreateAsync(applyMigrations: true)`, traits `RequiresDocker` + `PostgresRealCollection`). Escenario sembrado por `SalesDbContext`: métodos card (no efectivo, VES) y efectivo; regla `PaymentMethodFeeRule` card 1 % USD con vigencia previa (Percentage 9,4); venta A 100/5000 con ítem costo 60 y pago card fee 1,0000/50,00; venta B con costo desconocido y pago efectivo 50/2500; avance C con comisión 10/500 y pago card 510/25500 fee 5,1000/255,00; venta D pre-período anulada en la ventana con fee 2,0000/100,00; venta a crédito E 100/5000 con costo 60, `Receivable`/`ReceivablePayment`/`CreditPaymentAllocation` 40/2000 fee 0,4000/20,00 y `FxDifferenceBsS=400`; devolución parcial de A (`ReturnedUSD/RefundedUSD=10`, `RefundedBsS=500`); caja chica activa 5/250 con sesión de caja. Resultado Dual USD con ventana local UTC-4 [2026-11-02T04Z, 2026-11-03T04Z): Accrued bruto 250, anulaciones 50, devoluciones 10, neto 190, ingreso con costo 140, desconocido 50, CMV 94, utilidad bruta 46 (32,8571 %), comisiones 10, caja chica 5, fees 4,50 (incluye reversa S10 de 2,00), utilidad neta 46,50 y FX informativo 8; cobertura de costo 73,6842 % y fee 100 % (base conocida 650); Realized cobros 180, conocido 130, desconocido 50, CMV 84, utilidad bruta 46 (35,3846 %), FX 8, fees 4,50, utilidad neta 54,50; desgloses de métodos (card 650/fee 4,50 y efectivo 50), productos (E 40, A 36, B 0, D -30), categorías, vendedor único (190/46) y serie de un día reconciliada (190/46,50 y 180/54,50). Se certifican las columnas `FeeAmountUsd` 18,4 y `FeeAmountLocal` 18,2 con valores textuales 1.0000|50.00, 5.1000|255.00, 2.0000|100.00 y 0.4000|20.00, `PaymentMethodFeeRules.Percentage` 9,4, las 3 FKs de comisión y las dos migraciones F1 en el historial. Segundo test con `SET LOCAL enable_seqscan = off`: EXPLAIN (ANALYZE, BUFFERS) usa `Index Scan` en `IX_Sales_SaleType_Status_Date`, `IX_SalePayments_CreatedAt`, `IX_ReceivablePayments_ReceivedAtUtc`, `IX_CreditReturns_CreatedAtUtc` y `IX_PettyCashExpenses_CreatedAtUtc`, sin Seq Scan. Evidencia: build Release 0/0; PostgreSQL 2/2; filtro Profitability* 67/67; drift 4/4. Sin cambios de comportamiento productivo, sin migraciones aplicadas a la base real y sin commit.

- L19 (2026-11-10): **F2 CERRADA**. Verificación final del padre: build Release 0/0; suite completa `CommandCenter.Tests` con `TEST_POSTGRES_CONNECTION` **4349/4349, 0 skipped, 5m29s**; `ProfitabilityPostgresTests` 2/2; filtro Profitability* 67/67; drift 4/4. ANEXO LXII registrado. F2 queda cerrada; F3 (API/RBAC/exportación) y F4 (UI) son el siguiente alcance. Residuales: partials Accrued/Realized >500 líneas, desglose por método no aplica filtros de cajero/tipo/canal a fees/FX de CxC, EXPLAIN con seed pequeño y `enable_seqscan=off`.

- L20 (2026-11-10): **F3 aprobada y en ejecución**. El mantenedor aprobó F3 (API REST, RBAC y exportación server-side) con los WUs: F3-WU1 controladores y permisos; F3-WU2 CSV/XLSX; F3-WU3 PDF 1.4 nativo; F3-WU4 integración HTTP/RBAC, contenido de exports y ANEXO LXIII. Decisión sobre la consulta de Caja Chica: **sí**, los reportes PDF/Excel/CSV incluyen el desglose de gastos de Caja Chica por categoría como sección/pestaña de detalle; el KPI principal `PettyCashExpenses` sigue global y no se prorratea por artículo.

- L21 (2026-11-10): **F3-WU1 COMPLETADA**. Se agregaron las políticas `RequireAdminOrManager`/`RequireAdmin`, `ProfitabilityController` (`summary`/`breakdown`, Admin/Manager, `CancellationToken` y filtros query), `PaymentMethodFeesController` (CRUD Admin, baja lógica) y `PaymentMethodFeeRuleService` con validación de método no efectivo, vigencia, solapamientos activos, porcentaje `(0,100]`, fijo `>=0`, moneda USD/local y auditoría. `IProfitabilityService.GetBreakdownAsync` y `ProfitabilityBreakdownDto.Alerts` exponen el desglose con alertas. Evidencia: build Release 0/0; `PaymentMethodFeeRuleServiceTests` + `ProfitabilityHttpRbacTests` 20/20; filtro Profitability* + RBAC 87/87; drift 4/4. La matriz HTTP valida 401 anónimo, 403 Cashier, Manager 200 en lectura y 403 en fees, Admin 200/201/200/204; el filtro `Custom` 2026-11-01..02 se traduce a `[2026-11-01T04Z, 2026-11-03T04Z)` en zona global UTC-4. El controlador devuelve 400 ante `InvalidOperationException` de catálogo ausente.
