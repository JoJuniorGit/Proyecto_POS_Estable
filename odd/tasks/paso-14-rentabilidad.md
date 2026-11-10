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
- F1-WU5 | S6 | Índice `CreditReturn.CreatedAtUtc` e integración PostgreSQL | delegated | pending
- F1-WU6 | S1-S12 | Verificación integral F1 y evidencia | delegated | pending

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
