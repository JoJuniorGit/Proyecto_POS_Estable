# Facturas de Proveedor — Reactividad global: recálculo en cascada de la tabla Staging al cambiar Moneda o Tasa de Cambio

Objetivo: garantizar que la modificación de la Moneda del documento o de la Tasa aplicada en la cabecera, posterior a la carga/escaneo de la factura, dispare el recálculo en tiempo real de Costo USD y precios sugeridos en todas las filas de la tabla de pre-visualización, con título de columna dinámico y salvaguarda de división por cero. Tramo 8.176. Branch: V0.15. Entrega: work units locales en V0.15 (push/PR = decisión del mantenedor). RDD: off (clone-local). Tests: build `dotnet build CommandCenter.slnx -c Release`; carril `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice"`.

## Specs

Fuente: pedido del usuario (L1). Supuestos marcados como tal.

- **S1 (Suscripción de estado / two-way binding)**: "El selector de Moneda del documento (Bs.S / USD) y el input numérico de Tasa aplicada deben emitir un evento de cambio de estado a la colección de filas (Grid/Tabla)." Base 8.172 ya propaga; este tramo la corrige para que la moneda VIVA de la cabecera sea la fuente de verdad de la interpretación en revisión y las líneas se recalculen en bloque.
- **S2 (Recálculo inmediato en bloque)**: "Al cambiar la moneda de Bs.S a USD (o viceversa), el sistema conservará el valor extraído en la columna Costo factura, pero recalculará la columna Costo USD para todas las filas de la siguiente manera: Si es USD: Costo USD = Costo factura / Factor. Si es Bs.S: Costo USD = (Costo factura / Tasa aplicada) / Factor." El recálculo actualiza comparativa Costo anterior → nuevo, precios sugeridos (USD y Bs.S) y, al abrirse, los modales de producto vinculado (toman el `UnitCostUSD` recalculado de la línea).
- **S3 (Mutación visual de la cabecera)**: "El título de la columna Costo factura debe ser dinámico. Si el usuario selecciona Bs.S en la cabecera, la columna debe titularse Costo factura (Bs.S). Si selecciona USD, debe mutar a Costo factura (USD)." `DocumentCostHeader` pasa a depender de `IsBsSCurrency`; `LoadStagedInvoice` alinea el selector con la moneda del documento cargado (flujo real: stage/confirm/link devuelven la moneda vigente).
- **S4 (Salvaguarda de división por cero)**: "Si el usuario selecciona Bs.S pero borra la Tasa aplicada (dejándola en blanco o 0), la tabla no debe arrojar errores NaN o Infinity. Debe inhabilitar temporalmente el botón de Confirmar y Procesar Factura y mostrar 0,00 (o un guion -) en la columna Costo USD hasta que se ingrese una tasa válida." (Ya bloqueado por `HasValidAppliedRate`; ahora también las filas propagan tasa 0 y `Costo USD` muestra 0,00 en lugar de valores obsoletos.)
- **S5 (Coherencia preview↔confirm) (supuesto)**: derivado de S2/S4 — el confirm viaja con la moneda viva de la cabecera (`ConfirmSupplierInvoiceRequestDto.Currency`) y la tasa vigente; el backend conmuta el snapshot del draft y renormaliza los costos de las líneas aprobadas, de modo que lo procesado coincide con lo que muestra la tabla. Sin este ajuste, un cambio de moneda posterior al staging quedaría solo en la grilla y el confirm usaría la moneda del staging (divergencia silenciosa).
- **S6 (Proceso)**: build 0/0, carriles de test por partes en verde (restricción RAM del mantenedor), verificación independiente read-only, ANEXO 8.176 en `docs/reporte.txt`, commits en V0.15.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1–S4 | inline | Cliente: `DocumentCostHeader` por selector + notificación, `LoadStagedInvoice` alinea `SelectedCurrency`, `PropagateCurrencyAndRateToLines` con moneda viva y tasa inválida → (Bs.S, 0), `CalculateUnitCostUsd` Bs.S sin tasa → 0m, `ConfirmInvoiceAsync` envía moneda viva | hecho — RED 4/4 (por la causa correcta) → GREEN; carril SupplierInvoice 282/282; build slnx 0/0 |
| T2 | S5 | inline | Contrato: `ConfirmSupplierInvoiceRequestDto.Currency` (nullable) + conmutación de snapshot (moneda/tasa) y renormalización en `SupplierInvoiceService.Apply.cs` + validaciones zero-trust | hecho — RED compile-level → GREEN; 4 casos backend en el archivo de tests del tramo; regresión Apply 0 fallas |
| T3 | S1–S5 | inline | Tests: nuevo `SupplierInvoiceCurrencyReactivityTests.cs` (cliente + backend, RED observado) | hecho — 10/10 verde; Unit 1804/1804 + resto 571/571 (2375/2375, 0 fallas) |
| T4 | S6 | inline (+ verify delegado) | Build + carriles completos + verificación independiente (agente reviewer) + ANEXO 8.176 + commits | hecho — verificación independiente PASS: S1–S4 CONFORME, 0 blockers, 2 residuales (C1–C2 del ANEXO); ANEXO 8.176; commits feat + docs(8.176) en V0.15 |

Estrategia de entrega: `ask-on-risk` → work units locales en V0.15; PR/cadena = decisión del mantenedor. Forecast ~350–500 líneas autoradas (cliente ~70 + contrato ~55 + tests ~280 + docs). Bajo el umbral de cadena.

Nota de ruteo (observabilidad ODD): la exploración completó el mapa (Δ de implementación entendido y acotado a 5 archivos + tests nuevos); se ejecuta inline para no introducir deriva en semántica financiera ya resuelta, con verificación independiente delegada al cierre. Los tests nuevos van en archivo propio para no entrelazar el commit con el trabajo sin commitear de ingesta unificada presente en el árbol (archivos compartidos: `SupplierInvoiceViewModel.cs`, `SupplierInvoiceViewModel.Staging.cs`; el staging del commit usa `git apply --cached` con solo los hunks propios).

## Log

- L1 (2026-10-13) — Pedido del usuario, verbatim:

> Reactividad global: Recálculo en cascada de la tabla Staging al cambiar Moneda o Tasa de Cambio
>
> Objetivo:
> Garantizar que la modificación de la Moneda del documento o la Tasa aplicada en la cabecera, posterior a la carga o escaneo de la factura, dispare un recálculo automático y en tiempo real de los costos normalizados (Costo USD) y precios sugeridos en todas las filas de la tabla de pre-visualización.
>
> Criterios de Aceptación:
> 1. Suscripción de Estado (Two-Way Binding): El selector de Moneda del documento (Bs.S / USD) y el input numérico de Tasa aplicada deben emitir un evento de cambio de estado a la colección de filas (Grid/Tabla).
> 2. Recálculo Inmediato en Bloque: Al cambiar la moneda de Bs.S a USD (o viceversa), el sistema conservará el valor extraído en la columna Costo factura, pero recalculará la columna Costo USD para todas las filas de la siguiente manera: Si es USD: Costo USD = Costo factura / Factor. Si es Bs.S: Costo USD = (Costo factura / Tasa aplicada) / Factor. Este recálculo actualizará instantáneamente la comparativa de Costo anterior → nuevo y los precios de venta en los modales de los productos vinculados.
> 3. Mutación Visual de la Cabecera (UI): El título de la columna Costo factura debe ser dinámico. Si el usuario selecciona Bs.S en la cabecera, la columna debe titularse Costo factura (Bs.S). Si selecciona USD, debe mutar a Costo factura (USD).
> 4. Salvaguarda de División por Cero: Si el usuario selecciona Bs.S pero borra la Tasa aplicada (dejándola en blanco o 0), la tabla no debe arrojar errores NaN o Infinity. Debe inhabilitar temporalmente el botón de Confirmar y Procesar Factura y mostrar 0,00 (o un guion -) en la columna Costo USD hasta que se ingrese una tasa válida. En facturas de proveedor.

- L2 (2026-10-13) — Estado encontrado y decisiones de diseño (exploración read-only):
  - 8.172 ya propaga moneda/tasa a las líneas, pero con la condición `IsBsSCurrency || IsBsSInvoice`: una factura Bs.S staged ignora un flip del selector a USD, y con tasa inválida hace `return` temprano dejando valores obsoletos en vez de 0.
  - `ConfirmSupplierInvoiceRequestDto` no transporta moneda: el backend usa el snapshot del staging (`invoice.Currency`) y solo acepta override de tasa para Bs.S → un flip de moneda posterior al staging divergiría entre preview y confirm (S5).
  - `LoadStagedInvoice` alineará `SelectedCurrency` con la moneda del documento servido (mantiene consistente el título dinámico y los flujos stage/confirm/link en los que el servidor es el eco de la selección).
  - Tasa inválida en Bs.S: propagar `(Bs.S, 0m)` a las filas; `CalculateUnitCostUsd` retorna 0m sin tasa (se retira el fallback que escalaba el `UnitCostUSD` de origen, que solo era alcanzable en este estado); `HasValidAppliedRate` mantiene el confirm bloqueado.
  - El pill semántico (`CostChangeKind`/`CostComparisonPillText`) pertenece al trabajo sin commitear de ingesta unificada en el árbol; el pulido de ese pill para el estado sin tasa queda fuera de este tramo (limitación anotada).
  - Los tests nuevos van en archivo propio; el commit se arma con `git apply --cached` de hunks propios para no arrastrar los ~68 archivos de trabajo en curso ajenos al tramo.

- L3 (2026-10-13) — T1–T3 ejecutados (inline, diseño completo tras la exploración):
  - T1 cliente: `DocumentCostHeader` → `IsBsSCurrency` con notificación en `OnSelectedCurrencyChanged`; `LoadStagedInvoice` alinea `SelectedCurrency` antes del prefill de tasa y la construcción de líneas; `PropagateCurrencyAndRateToLines` reescrita (se retiró `|| IsBsSInvoice`), con tasa inválida propaga `(Bs.S, 0m)` a todas las filas y `SyncStagedSnapshot` alinea el snapshot local; `CalculateUnitCostUsd` Bs.S sin tasa → `0m` (se retiró el fallback por ratio, inalcanzable fuera de ese estado); `ConfirmInvoiceAsync` envía `SelectedCurrency`. RED observado 4/4 (título por selector, alineación al cargar, cascada Bs.S↔USD, cero con tasa inválida) → GREEN.
  - T2 contrato: `ConfirmSupplierInvoiceRequestDto.Currency` (posicional final, default null, retrocompatible) + validación zero-trust USD/Bs.S y conmutación de snapshot en `SupplierInvoiceService.Apply.cs` (flip a Bs.S exige tasa explícita; flip a USD fuerza tasa 1; `currencyChanged ⇒ appliedRateChanged` renormaliza las líneas aprobadas vía `ApplyCorrection`/`ApplyConversionFactor` con la fórmula compartida de `PricingCalculator`). RED compile-level → GREEN.
  - T3 tests: nuevo `SupplierInvoiceCurrencyReactivityTests.cs` con 10 casos (4 cliente + 2 payload de confirm + 3 backend de conmutación/validación + 1 alineación). Evidencia: carril SupplierInvoice 282/282; Unit 1804/1804 + resto 571/571 = 2375/2375, 0 fallas; `dotnet build CommandCenter.slnx -c Release` 0/0.
  - Staging del commit: blobs propios reconstruidos con `git hash-object`/`update-index` sobre la base HEAD (verificado: la copia "HEAD + 8.176" difiere del worktree exactamente en las inserciones del WIP ajeno, 114 y 55 sin deleciones) — el WIP de ingesta unificada queda intacto en el working tree.

- L4 (2026-10-13) — Cierre: verificación independiente read-only (agente reviewer) PASS: S1–S4 CONFORME, S5 mecanismo correcto con 2 residuales no bloqueantes (C1: `SyncStagedSnapshot` conserva la tasa previa en el snapshot local solo-display cuando la tasa de cabecera es inválida; C2: divergencia de precisión 2/4 decimales pre-existente de `PricingCalculator` entre preview y persistencia con factor >1 en USD). ANEXO 8.176 registrado en `docs/reporte.txt`; commits feat `3298d85` + docs(8.176) en V0.15. Push/PR: decisión del mantenedor (no realizados).
