# Stock Kardex Enterprise UI (7-Column Audit Grid)

Branch: V0.15. Delivery: local work-unit commits; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`. RDD: off (clone_local).

## Specs

S1. Unified header: audit/kardex icon inside the standard badge (`42x42px`, `CornerRadius="10"`, `Background="#EDE9FE"`, icon `#6D28D9`). Title `Kardex — Auditoría de Inventario` (`22px`, `Bold`, `#0F172A`) plus subtitle `Trazabilidad completa de entradas, salidas, mermas y ajustes de stock con impacto valorizado.` (`13px`, `#64748B`). Right-side `Actualizar` and menu buttons aligned (`Height="38"`, `CornerRadius="8"`, `#FFFFFF`, border `#CBD5E1`, foreground `#334155`, hover `#F1F5F9`). Preserve `StockKardex_RefreshButton` and the menu style.

S2. Filter/export card: white (`#FFFFFF`, border `#E2E8F0`, `CornerRadius="12"`, `Padding="16,14"`), no heavy shadow.
- Row 1 fields (`Height="38"`, border `#CBD5E1`, focus `#6D28D9`, `CornerRadius="8"`):
  - `Desde` / `Hasta` DatePickers with vertically centered text (`VerticalContentAlignment="Center"`), soft `#CBD5E1` chrome and no near-black borders.
  - `Producto (nombre o código)` search with an internal `×` button that clears the text and closes suggestions; keep the predictive suggestions popup.
  - `Motivo`, `Origen`, `Sentido` clean ComboBoxes with `ComboBoxItem` hover/selection `#EDE9FE`/`#5B21B6`.
- Row 2 separated by a subtle top divider (`Margin="0,12,0,0"`, border `#F1F5F9`, `Padding="0,12,0,0"`):
  - Left: `Hoy`, `Esta Semana`, `Mes Actual` as a segmented chip group (`Background="#F1F5F9"`, `CornerRadius="8"`, `Padding="3"`; active chip `#FFFFFF` or `#EDE9FE`/`#6D28D9`), not three purple-outlined boxes. `Limpiar` neutral secondary (`#FFFFFF`, border `#CBD5E1`, text `#475569`, `Height="34"`, `CornerRadius="8"`). `Aplicar Filtros` flat primary (`#6D28D9`, hover `#5B21B6`, white, `Height="34"`, `Padding="16,0"`, `CornerRadius="8"`, no heavy dark shadow) with the existing automation IDs.
  - Right: muted `Exportar:` label (`12px`, `#64748B`) plus three compact icon buttons (`Height="34"`, `CornerRadius="8"`, `Padding="10,0"`): `CSV` neutral (`#FFFFFF`, border `#CBD5E1`, `#334155`, hover `#F1F5F9`), `Excel` emerald (`#ECFDF5`, border `#A7F3D0`, `#047857`, hover `#D1FAE5`), `PDF` coral (`#FEF2F2`, border `#FECACA`, `#B91C1C`, hover `#FEE2E2`). Preserve `StockKardex_ExportCsv/Excel/Pdf`, `CanExport`, and all export commands.

S3. Five-KPI financial bar: one white bar (`#FFFFFF`, border `#E2E8F0`, `BorderThickness="1"`, `CornerRadius="12"`) split into five equal cells with 1px `#F1F5F9` vertical dividers (`Margin="0,12"`), labels uppercase `11px` `SemiBold` `#64748B`, values tabular `21px` `Bold`:
1. `MOVIMIENTOS` (`#0F172A`).
2. `VOLUMEN NETO` (`#0F172A`, `#DC2626` when negative, `#15803D` when positive; e.g. `-182,3 uds.`).
3. `ENTRADAS (USD)` (`#15803D` when `> 0`, `#64748B` when `0`), dynamic profile currency code.
4. `MERMAS / SALIDAS (USD)` (`#DC2626` when `> 0`, `#64748B` when `0`), dynamic profile currency code.
5. `BALANCE NETO (USD)` in a subtle `#F5F3FF` right-hand cell with the value in `#6D28D9`, dynamic profile currency code.
Preserve the unvalued-movements warning below the bar (audit integrity).

S4. Critical signed-money format: negatives always render the sign before the currency symbol (`-$193,26`), never `$-193,26`. This applies to KPIs and every table/row-detail amount. Positive impact values in the table render with a leading `+` (`+$12,50`); zero renders `$0,00` in muted `#475569`. Unit costs and KPIs keep the currency symbol without a leading `+` for positives.

S5. Table: one white card (`#FFFFFF`, border `#E2E8F0`, `BorderThickness="1"`, `CornerRadius="12"`, `Height="*"`) containing both the grid and its pagination footer.
- Virtualized `DataGrid` (`VirtualizingStackPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"`, no external `ScrollViewer`), `HeadersVisibility="Column"`, `GridLinesVisibility="Horizontal"`, `HorizontalGridLinesBrush="#F1F5F9"`, `RowHeight="56"`, `BorderThickness="0"`, header `#F8FAFC` (`11px`, `SemiBold`, `#64748B`, bottom border `#E2E8F0`), thin `6px` scrollbar.
- Exactly seven consolidated columns, keeping every auditable field:
  1. `FECHA / REF.` (`Width="160"`, left): `dd/MM/yy · HH:mm:ss` local date/time (`12.5px`, `SemiBold`, `#0F172A`, tabular) + full reference (`#venta 2620`, `11.5px`, `#64748B` or `#6D28D9` when sale/invoice), ellipsis with tooltip.
  2. `PRODUCTO` (`Width="*"`, `MinWidth="240"`, left): full product name (`13px`, `SemiBold`, `#0F172A`, ellipsis + tooltip) + `SKU: {Sku}` (`11.5px`, tabular `#64748B`).
  3. `MOVIMIENTO` (`Width="150"`, left): semantic reason pill (`CornerRadius="999"`, `Padding="8,2"`): `Venta` (`#F1F5F9`/`#334155`), `Compra`/`Entrada` (`#DCFCE7`/`#15803D`), `Merma`/`Salida` (`#FEE2E2`/`#DC2626`), `Conteo Físico` (`#EDE9FE`/`#5B21B6`), `Devolución`/`Ajuste` (`#FEF3C7`/`#B45309`); second line `Origen: {Origen}` (`11.5px`, `#64748B`).
  4. `VARIACIÓN` (`Width="95"`, centered header/content): tabular bold pill never clipping the sign: negative (`-179`, `-1,3`) `#FEF2F2`/`#DC2626`, positive (`+10`) `#F0FDF4`/`#15803D`, zero `#F1F5F9`/`#64748B`.
  5. `TRAZABILIDAD` (`Width="150"`, centered): `StockAntes → StockResultante`; before `12.5px` `#64748B`, arrow `#94A3B8` with `Margin="6,0"`, result `13px` `Bold` `#0F172A`; a negative result `< 0` is highlighted red (`#FEF2F2`/`#DC2626`) as an alert.
  6. `IMPACTO (USD)` (`Width="140"`, right-aligned header/values): total impact with the sign before the symbol (`-$175,42`, `+$12,50`, `13px`, `Bold`, tabular; `#DC2626` negative, `#15803D` positive, `#475569` zero); second line `Unit: $0,98` (`11.5px`, tabular `#64748B`).
  7. `AUDITORÍA` (`Width="170"`, left): executor (`12.5px`, `#334155`, e.g. name or `Usuario #4`); `Aut: {Autorizador}` only when present; a discreet `Nota` chip (`#F5F3FF`/`#6D28D9`) with the full observation as tooltip when `Observación` has content.
- Selected-row detail (`RowDetailsTemplate` or compact strip at the inner bottom of the grid): `#F8FAFC` background, top/bottom border `#E2E8F0`, showing all extended audit metadata: exact date (local + UTC), SKU, full product, executor, authorizer, full reference, full observation, reason/origin, stock before/result, unit cost and impact. No auditable field stays hidden.
- Preserve `StockKardex_Grid`.

S6. Pagination integrated inside the table card: footer with `BorderThickness="0,1,0,0"`, border `#F1F5F9`, background `#FAFAFA`, `CornerRadius="0,0,12,12"`, `Padding="16,10"`; `PageSummary`/count on the left and the `|<`, `<`, `>`, `>|` controls on the right. Preserve `StockKardex_PageSummary`, `StockKardex_PreviousPage`, `StockKardex_NextPage` and the existing paging commands. The error message may live in the footer on the left, never as a floating box.

S7. Empty state: strictly `Collapsed` when `Rows.Count > 0`; visible only with zero rows and not busy. Centered muted icon, title exactly `Sin movimientos para los filtros seleccionados`, and a quick `Limpiar filtros` button bound to `ClearFiltersCommand`.

S8. ViewModel surface (contract for the view; no API or export changes):
- Preserve all existing filters, paging, access, export, and load semantics.
- Add `ActiveDatePreset` (`StockKardexDatePreset { Custom, Today, ThisWeek, CurrentMonth }`, default `Today`); the three preset commands set it, manual date edits set `Custom`, and `ClearFiltersCommand` resets to `Today`.
- Add `ClearProductSearchCommand` (clears text, selection, suggestions and closes the popup).
- Add raw KPI state for conditional colors: `NetVolumeValue`, `EntriesValueUsd`, `LossesValueUsd`, `NetValueUsd`, `IsNetVolumeNegative`, `IsNetVolumePositive`, `HasEntriesValue`, `HasLossesValue`, notified when the summary is applied.
- Add signed formatting helpers: `FormatSignedPrimary(decimal)` (negative sign before symbol; positives without `+`) and `FormatImpactValue(decimal)` (negative sign before symbol; positives with `+`; zero no sign). Use them in `ApplySummary` and row amounts.
- Add row presentation members: `DateCompactText`, `HasReference`, `ReasonPillLabel`, `MovementKind` (`StockKardexMovementKind { Unknown, Sale, Entry, Loss, Count, Adjustment }`), `IsNegativeQuantity`, `IsPositiveQuantity`, `IsZeroQuantity`, `IsResultNegative`, `ImpactKind` (`StockKardexImpactKind { None, Zero, Positive, Negative }`), `UnitCostInlineDisplay`, `HasAuthorizer`, `AuthorizerInlineDisplay`, `HasObservation`, `ObservationTooltip`, `SourceDisplay`. Keep the existing display members (`DateLocalText`, `DateUtcText`, `Sku`, `ProductName`, `ProductDisplay`, `DeltaText`, `StockBeforeText`, `NewStockText`, `UnitCostDisplay`, `TotalValueDisplay`, `OriginLabel`, `ReasonLabel`, `ReasonTooltip`, `ExecutedByDisplay`, `AuthorizedByDisplay`, `ReferenceDisplay`, `ObservationDisplay`) for row details and compatibility.
- Reason-to-kind mapping: Sale → Sale; PurchaseEntry/InitialLoad → Entry; Damage/Expiry → Loss; PhysicalCountCorrection → Count; Return/ImportAdjustment → Adjustment; unknown → Unknown.

S9. Tests: add unit tests for negative/positive/zero signed formatting (KPI and row), impact kind, reason pill/kind mapping, result-negative flag, date compact text, authorizer/observation flags, `ClearProductSearchCommand`, and date preset state. Update the existing positive `TotalValueDisplay` expectation to the new leading `+`. Keep the Kardex, export, and UI client tests green.

S10. Validation: `dotnet build CommandCenter.slnx -c Release` with `0 warnings / 0 errors`; Kardex and export tests green; offscreen WPF render at 100% and 125% DPI showing full product names (tooltip/row details), no clipped negative signs, no `$-` ordering, and the seven columns filling the table without forced horizontal scrolling at the target widths.

S11. Constraints: no new external libraries, no dense black `DropShadowEffect`, no `#000000` lines, no API/filter/export contract changes, no reduced export columns, no hidden auditable fields.

Assumptions:
- A1. Positive sign `+` is required for the impact column; KPIs and unit-cost lines keep the plain positive currency format.
- A2. The main grid uses ellipsis + tooltip for long names, while the selected-row details always show the full value; numbers and signs must never clip.
- A3. The seven fixed column widths total 865px plus the `PRODUCTO` minimum 240px; at the app's minimum window a horizontal scrollbar is an acceptable fallback, but the target layout (1280+ DIPs and 125% DPI on a wide display) must fit without forced horizontal scrolling.
- A4. Exports and their detailed columns are backend-owned and untouched.
- A5. `Nota` appears only when observation text exists; `Aut:` appears only when an authorizer exists.
- A6. The signed formatter uses `MoneyFormat.N2` magnitude plus the profile primary currency symbol and the es-VE negative sign, preserving the existing dynamic currency profile.

## Tasks

- [x] T1 (S3, S4, S8, S9) - ViewModel/row presentation layer: signed formatting, KPI raw state, date presets, clear-product command, semantic row flags, row-detail members, and unit tests. Route: delegated writer + parent singular-warning fix. RED: missing-member compile failure; GREEN: 98/98 focused Kardex lane after the singular test. Commit 68d045.
- [x] T2 (S1, S2, S5, S6, S7) - Rewrite `StockKardexView.xaml`: unified header, filter/export card, five-cell KPI bar, seven-column grid with row details, integrated pagination and empty state. Route: delegated writer + parent visual fixes (MinWidth/scroll, placeholder, dynamic impact header, audit grid, compact row details). Build 0/0. Commit 895e29.
- [x] T3 (S10) - Release build, focused/full test lanes, offscreen 100%/125% DPI visual validation, fixes and final work-unit commit. Route: parent. Build 0/0; ViewModel 48/48; export subset 8/8; combined Kardex lane 98/98 before the final XAML-only polish; DatePicker popup verified as Calendar/open; harness at 1280/1600/1024 and 125% DPI.

## Log

L1. User request (verbatim):

Actúa como un Principal WPF UI/UX & MVVM Architect. Ejecuta una mejora visual integral nivel Masterchip sobre la vista **Kardex — Auditoría de Inventario** (`KardexView.xaml` / vista equivalente y su ViewModel), reduciendo la tabla de **14 columnas apiñadas a 7 columnas limpias de alta densidad** sin perder absolutamente ningún campo auditable.

**Reglas inquebrantables:**
- Respeta estrictamente la paleta institucional (`#F8FAFC` fondo base, `#FFFFFF` tarjetas, `#E2E8F0` bordes de tarjeta, `#CBD5E1` bordes de inputs, `#6D28D9` primario, `#5B21B6` hover, `#EDE9FE`/`#F5F3FF` suave, `#0F172A` texto primario, `#64748B` texto secundario).
- **Prohibido:** librerías externas nuevas, sombras negras pesadas (`DropShadowEffect` densos), líneas `#000000`, recortes de texto/números y romper contratos de API, filtros o exportadores (`CSV`, `Excel`, `PDF`).

### 1. Cabecera Unificada
- Envuelve el ícono de auditoría/kardex en el badge estándar (`42×42px`, `CornerRadius="10"`, `Background="#EDE9FE"`, ícono `#6D28D9`).
- Título: `Kardex — Auditoría de Inventario` (`22px`, `Bold`, `#0F172A`) + subtítulo: `Trazabilidad completa de entradas, salidas, mermas y ajustes de stock con impacto valorizado.` (`13px`, `#64748B`).
- Botones derechos `Actualizar` y menú alineados (`Height="38"`, `CornerRadius="8"`, `#FFFFFF`, borde `#CBD5E1`, `#334155`, hover `#F1F5F9`).

### 2. Tarjeta de Filtros y Exportación Limpia (Adiós Bordes Negros y Ruido Morado)
- Contenedor blanco (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`, `Padding="16,14"`).
- **Fila 1 — Campos de filtro (`Height="38"` DPI-safe, borde `#CBD5E1`, foco `#6D28D9`, `CornerRadius="8"`):**
  * **Corregir `DatePicker` (`Desde` / `Hasta`):** Alinea el texto de fecha estrictamente al centro vertical (`VerticalContentAlignment="Center"`) y suaviza su borde a `#CBD5E1` (adiós texto subido y bordes casi negros).
  * `Producto (nombre o código)`: incluye botón interno `×` para limpiar texto rápido.
  * `Motivo`, `Origen` y `Sentido`: `ComboBox` limpios con `ComboBoxItem` estilizados en `#EDE9FE` / `#5B21B6`.
- **Fila 2 — Separada por un divisor sutil (`Margin="0,12,0,0"`, borde superior `#F1F5F9`, `Padding="0,12,0,0"`):**
  * **Izquierda (Presets rápidos + Acciones de filtro):**
    - Convierte `Hoy`, `Esta Semana` y `Mes Actual` en un **Segmented Control / chips suaves** (`Background="#F1F5F9"`, `CornerRadius="8"`, `Padding="3"`, chip activo en `#FFFFFF` o `#EDE9FE`/`#6D28D9`, eliminando los 3 cuadros con borde morado).
    - `Limpiar`: botón secundario neutro limpio (`#FFFFFF`, borde `#CBD5E1`, texto `#475569`, `Height="34"`, `CornerRadius="8"`).
    - `Aplicar Filtros`: botón primario plano (`Background="#6D28D9"`, hover `#5B21B6`, `Foreground="#FFFFFF"`, `Height="34"`, `Padding="16,0"`, `CornerRadius="8"`, **sin sombra oscura pesada**).
  * **Derecha (Exportadores con jerarquía propia y semántica clara):**
    - Etiqueta tenue `Exportar:` (`12px`, `#64748B`) seguida de 3 botones compactos con ícono (`Height="34"`, `CornerRadius="8"`, `Padding="10,0"`):
      * `CSV` (neutro `#FFFFFF`, borde `#CBD5E1`, `#334155`, hover `#F1F5F9`)
      * `Excel` (esmeralda sutil `#ECFDF5`, borde `#A7F3D0`, `#047857`, hover `#D1FAE5`)
      * `PDF` (rojo/coral sutil `#FEF2F2`, borde `#FECACA`, `#B91C1C`, hover `#FEE2E2`)

### 3. Franja de 5 KPIs con Divisores y Formato Negativo Corregido
- **Corrección crítica de signo monetario:** Corrige el formato en KPIs y tabla para que los negativos muestren siempre **`-$193,26`** (signo antes del símbolo monetario) y nunca `$-193,26`.
- Estiliza los 5 KPIs en una **barra financiera unificada** (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`) dividida en 5 celdas iguales separadas por líneas verticales tenues (`1px`, `#F1F5F9`, `Margin="0,12"`), con etiqueta superior en mayúsculas sutiles (`11px`, `SemiBold`, `#64748B`) y cifras en fuente tabular `21px` `Bold`:
  1. `MOVIMIENTOS` (`#0F172A`)
  2. `VOLUMEN NETO` (`#0F172A` / `#DC2626` si negativo / `#15803D` si positivo, ej. `-182,3 uds.`)
  3. `ENTRADAS (USD)` (`#15803D` si `> 0`, `#64748B` si `0`)
  4. `MERMAS / SALIDAS (USD)` (`#DC2626` si `> 0`, `#64748B` si `0`)
  5. `BALANCE NETO (USD)` (celda derecha con tinte sutil `#F5F3FF`, valor en `#6D28D9`)
  *(Mantén el código de moneda dinámico del perfil si ya está enlazado).*

### 4. Reestructuración de la Tabla: De 14 a 7 Columnas (Sin Perder Datos ni Recortes)
- Envuelve la tabla y su pie de paginación dentro de una **única tarjeta blanca** (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`, `Height="*"`).
- Usa `DataGrid` virtualizado (`VirtualizingStackPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"`, sin `ScrollViewer` externo) con `HeadersVisibility="Column"`, `GridLinesVisibility="Horizontal"`, `HorizontalGridLinesBrush="#F1F5F9"`, `RowHeight="56"`, `BorderThickness="0"`, cabecera `#F8FAFC` (`11px`, `SemiBold`, `#64748B`, borde inferior `#E2E8F0`) y `ScrollBar` delgado moderno (`Width="6"`).
- **Implementa exactamente estas 7 columnas consolidadas:**
  1. **`FECHA / REF.`** (`Width="160"`, izquierda):
     - Línea 1: Fecha y hora completas sin recortes (`dd/MM/yy · HH:mm:ss`, `12.5px`, `SemiBold`, `#0F172A`, fuente tabular).
     - Línea 2: Referencia completa (`#venta 2620`, `11.5px`, `#64748B` o `#6D28D9` si es factura/venta, `TextTrimming="CharacterEllipsis"` con `ToolTip`).
  2. **`PRODUCTO`** (`Width="*"`, `MinWidth="240"`, izquierda):
     - Línea 1: Nombre completo del producto (`13px`, `SemiBold`, `#0F172A`, `TextTrimming="CharacterEllipsis"` con `ToolTip`).
     - Línea 2: `SKU: {Sku}` (`11.5px`, fuente tabular `#64748B`).
  3. **`MOVIMIENTO`** (`Width="150"`, izquierda):
     - Línea 1: Píldora semántica de `Motivo` (`CornerRadius="999"`, `Padding="8,2"`): `Venta` (`#F1F5F9`/`#334155`), `Compra`/`Entrada` (`#DCFCE7`/`#15803D`), `Merma`/`Salida` (`#FEE2E2`/`#DC2626`), `Conteo Físico` (`#EDE9FE`/`#5B21B6`), `Devolución`/`Ajuste` (`#FEF3C7`/`#B45309`).
     - Línea 2: `Origen: {Origen}` (`11.5px`, `#64748B`).
  4. **`VARIACIÓN`** (`Width="95"`, encabezado y contenido centrados):
     - Píldora tabular `Bold` sin recortar nunca el signo: negativa (`-179`, `-1,3`) en `#FEF2F2`/`#DC2626`; positiva (`+10`) en `#F0FDF4`/`#15803D`; cero en `#F1F5F9`/`#64748B`.
  5. **`TRAZABILIDAD`** (`Width="150"`, encabezado y contenido centrados):
     - Flujo horizontal claro `StockAntes → StockResultante` en fuente tabular:
       * `StockAntes` en `12.5px` `#64748B`
       * Flecha `→` en `#94A3B8` (`Margin="6,0"`)
       * `StockResultante` en `13px` `Bold` `#0F172A` — si el stock resultante es **negativo (`< 0`)**, destácalo con píldora/texto rojo alerta (`#FEF2F2`/`#DC2626`) para detectar quiebres de inventario al instante.
  6. **`IMPACTO (USD)`** (`Width="140"`, encabezado y cifras alineados a la **derecha**):
     - Línea 1: Impacto total formateado con el signo antes del símbolo (`-$175,42`, `+$12,50`, `13px`, `Bold`, fuente tabular; `#DC2626` si negativo, `#15803D` si positivo, `#475569` si `0`).
     - Línea 2: Costo unitario (`Unit: $0,98`, `11.5px`, fuente tabular `#64748B`).
  7. **`AUDITORÍA`** (`Width="170"`, izquierda):
     - Línea 1: `Ejecutor` (`12.5px`, `#334155`, ej. nombre de usuario o `Usuario #4`).
     - Línea 2: `Aut: {Autorizador}` solo cuando exista valor (sin saturar con `—` innecesarios) + si `Observación` tiene contenido, muestra un chip/ícono discreto `Nota` (`#F5F3FF`/`#6D28D9`) con `ToolTip` del texto completo.
- **Panel de Detalle de Fila Seleccionada (Cero información oculta):**
  * Al hacer clic en cualquier movimiento de la tabla, despliega una tira compacta al pie interno de la grilla (o `RowDetailsTemplate` limpio con fondo `#F8FAFC` y borde superior `#E2E8F0`) mostrando en claro todos los metadatos extendidos de auditoría: `Fecha exacta`, `SKU`, `Producto completo`, `Ejecutor`, `Autorizador`, `Referencia completa` y `Observación completa`.

### 5. Pie de Paginación Integrado y Estado Vacío
- **Integrar el paginador dentro de la tarjeta de tabla:** Mueve `Página 1 de 1 (4 movimientos)` y los botones `|<`, `<`, `>`, `>|` desde el recuadro flotante exterior hacia el pie integrado de la tarjeta (`BorderThickness="0,1,0,0"`, `BorderBrush="#F1F5F9"`, `Background="#FAFAFA"`, `CornerRadius="0,0,12,12"`, `Padding="16,10"`), con el conteo a la izquierda y los controles a la derecha.
- **Estado Vacío:** Strictly `Collapsed` cuando haya movimientos; visible solo cuando `Count == 0` con ícono tenue, título `"Sin movimientos para los filtros seleccionados"` y botón rápido `Limpiar filtros`.

### 6. Verificación
- Compila con `dotnet build CommandCenter.slnx -c Release` verificando `0 warnings / 0 errors`.
- Confirma que todos los tests del Kardex y exportadores (CSV, Excel, PDF) sigan en verde —los archivos exportados deben conservar todas sus columnas detalladas— y valida visualmente a 100% y 125% DPI que: (1) el nombre de cada producto sea completamente legible, (2) ningún signo negativo se corte ni aparezca como `$-`, y (3) las 7 columnas llenen el 100% de la tabla sin scroll horizontal forzado.

L2. Baseline: HEAD `ae91045` (`feat(partial-pickup): redesign modal and add strict quantity input`), branch V0.15, RDD off. `StockKardexView.xaml` is a 14-column DataGrid with 38px rows, a floating pagination card, no integrated empty state, and no date-preset state. `StockKardexViewModel` owns filters, KPIs, pagination and exports; `StockKardexRowViewModel` exposes display strings and currently formats negative currency as symbol-before-sign (`$-193,26`). Existing tests: 12 facts in `StockKardexViewModelTests`, including positive KPI/row displays; export service tests are separate and must stay untouched.

L3. Assumptions recorded in A1-A6 (impact positive `+` only in the table; ellipsis + tooltip in the dense grid with full values in row details; fixed column widths with a narrow-window horizontal-scroll fallback; backend exports untouched; conditional `Aut`/`Nota`; es-VE magnitude formatting with the dynamic profile symbol).

L4. T1 executed by `gentle-ai-worker`: signed formatters (`FormatSignedPrimary`/`FormatImpactValue`), raw KPI state, date presets with a manual-edit guard, `ClearProductSearchCommand`, semantic row enums/flags, compact date, row-detail members, and 20+ new tests. The existing positive impact expectation was updated to the new `+$3,00` contract. Parent added the singular unvalued warning (`1 movimiento ... excluido`) plus its test. Final focused Kardex lane: 98/98; solution build 0/0.

L5. T2 executed by `gentle-ai-worker`: complete seven-column XAML with the custom borderless DatePicker template (verified at runtime: `PART_Popup.Child` is a `Calendar`, `IsDropDownOpen=true`), segmented presets, five-cell KPI bar, 7-column virtualized grid with selected-row audit details, integrated pagination, strict empty state and loading overlay. Parent visual review at 1280x820 (96 DPI), 1600x900, 1280x820 (125% DPI), 1093x700, 1024x720, negative-stock result, empty and loading states fixed: placeholder for product search, `MinWidth` on the fixed columns to stop header compression, dynamic `IMPACTO (currency)` header, audit authorizer/Nota grid, compact row-detail spacing, and singular warning.

L6. Verification: solution build `0 warnings / 0 errors`; `StockKardexViewModelTests` 48/48; export tests (`StockKardexServiceTests.ExportAsync`) 8/8; the combined `~StockKardex` lane was 98/98 before the final XAML-only polish (a later isolated run of the whole service class hit an environmental testhost OOM; the export subset re-ran green). The offscreen harness confirmed no `$-` ordering, `+$` positive impact, negative stock alert, full product name at 1280+, RowDetails with all fields, and the DatePicker chrome/text alignment; at the app's 1024-DIP minimum the exact 865px + 240px column set shows the horizontal scrollbar fallback (A3), while 1280+ fills without forced scrolling. Backend exports and their detailed columns were untouched.

L7. Concurrent sessions have unrelated working-tree changes/commits on V0.15; this work unit stages only `StockKardexView.xaml`, `StockKardexViewModel.cs`, `StockKardexViewModelTests.cs`, and this ODD document.

L8. Commits: 68d045 (T1 signed currency/seven-column presentation state + tests) and 895e29 (T2 enterprise seven-column view + ODD document). T3 verification remains parent-owned and produced no additional source changes.
