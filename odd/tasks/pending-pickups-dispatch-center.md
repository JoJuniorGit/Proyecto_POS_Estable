# Pending Pickups Dispatch Center (Enterprise UI)

Branch: V0.15. Delivery: local work-unit commits; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`. RDD: off (clone_local) at start.

## Specs

S1. Unified header: wrap the custody icon (package/box) in the system badge (`42x42px`, `CornerRadius="10"`, `Background="#EDE9FE"`, icon `#6D28D9`). Title `Retiros Pendientes (Mercancía en Custodia)` (`22px`, `Bold`, `#0F172A`) plus subtitle `Mercancía pagada al 100% pendiente de entrega física al cliente.` (`13px`, `#64748B`). Right-side `Refrescar` button and menu button aligned (`Height="38"`, `CornerRadius="8"`, `#FFFFFF`, border `#CBD5E1`, foreground `#334155`, hover `#F1F5F9`). Preserve `PendingPickups_RefreshButton` and `SidebarMenuButtonStyle`.

S2. Four-KPI strip computed in memory from the loaded ViewModel collection, with no new endpoints: `Grid` of four equal columns (`Margin="0,12,0,12"`, `12px` separation), white card (`#FFFFFF`, border `#E2E8F0`, `4px` left accent, `CornerRadius="12"`, `Padding="16,12"`, minimal shadow `Opacity="0.03"`).
- `CUSTODIAS ACTIVAS` (accent `#6D28D9`): total pending invoices (`22px`, `Bold`, `#0F172A`) plus subtitle with how many distinct customers have open pickups.
- `UNIDADES POR ENTREGAR` (accent `#D97706`): sum of all pending quantities (e.g. `181 uds.`) plus subtitle with product lines still to dispatch.
- `RETIROS PARCIALES` (accent `#0284C7`): count of invoices with `Entregado > 0` and pending balance, or `"0 parciales"` in a neutral tone when none.
- `VALOR EN CUSTODIA` (accent `#16A34A`): sum of pending paid value in `Bs.S` (`20px`, `Bold`, tabular numerals) plus `$` equivalent below, hidden in `Single` regime.

S3. Full toolbar/filter card (`#FFFFFF`, border `#E2E8F0`, `CornerRadius="12"`, `Padding="14,10"`, `Margin="0,0,0,12"`).
- Left search box (`MinWidth="340"`, `Width="*"`, `MaxWidth="460"`, `Height="38"`, `CornerRadius="8"`, `BorderBrush="#CBD5E1"`, focus `#6D28D9`) with placeholder exactly `Buscar por N° factura, cliente, cédula/RIF o producto...` and an internal `×` button that clears the text.
- Center segmented status filter: container `#F1F5F9` (`CornerRadius="8"`, `Padding="3"`) with three clickable pill buttons filtering locally: `Todas (N)`, `Sin retirar (X)` (`Entregado == 0`), `Parciales (Y)` (`Entregado > 0 && Pendiente > 0`).
- Right sort selector: clean DPI-safe `ComboBox` (`Height="38"`, `MinWidth="185"`) with `Más recientes primero` (default), `Más antiguas primero`, and `Mayor cantidad pendiente`.

S4. Replace the single card list with a Master-Detail workspace split (`Grid` columns `Width="3.8*"` with `MinWidth="360"`, `14px` gap, `Width="6.2*"`), occupying `Height="*"`. Both panels are white (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`).

S5. Left panel (`38%`) — scannable custody tray.
- Fixed header (`Height="44"`, `#F8FAFC`, bottom border `#E2E8F0`, `CornerRadius="12,12,0,0"`, `Padding="16,0"`) with title `Facturas en custodia` (`13.5px`, `SemiBold`, `#0F172A`) and a right pill counter (`N mostradas`).
- Virtualized `ListBox` (`VirtualizingStackPanel.IsVirtualizing="True"`, recycling, pixel scroll) with thin `6px` scrollbar.
- Smart auto-selection: after load or filtering, automatically select the first available invoice when `SelectedCustody` is null or fell outside the filter. After a completed pickup removes the selected row, select the next available row.
- Compact item (`Padding="14,12"`, bottom border `1px` `#F1F5F9`, hover `#F8FAFC`, selected `#F5F3FF` with a `3px` left bar `#6D28D9`):
  - Row 1: badge `Factura #00290` (`#EDE9FE`/`#5B21B6`, `CornerRadius="6"`, `Padding="8,2"`, `SemiBold`) + well-proportioned status pill (`CornerRadius="999"`, `Padding="8,2"`, no deformed oval: `● Sin retirar` in `#FEF3C7`/`#B45309` or `● Parcial` in `#DBEAFE`/`#1D4ED8`) + right-aligned main amount (`Bs.S 212,249.25`, `13px`, `Bold`, tabular).
  - Row 2: customer name (`13px`, `SemiBold`, `#0F172A`, `TextTrimming="CharacterEllipsis"` with `ToolTip`) + muted cedula/RIF (`#64748B`).
  - Row 3: volume pill (`179 uds. pendientes` in `#F1F5F9`/`#334155`, highlighted `#FFFBEB`/`#B45309` for high volume) + date/age (`27/11/26 · Hoy` or `Hace X días`, amber beyond 7 days) on the left; `$ 229.12` equivalent on the right, hidden in `Single`.
  - Row 4: subtle progress mini-bar (`Height="4"`, `#E2E8F0` track / `#16A34A` fill) reflecting `Entregado / Total`.

S6. Right panel (`62%`) — active dispatch panel for the selected invoice, structured in three rows (`Auto`, `*`, `Auto`).
- Dispatch header (`Background="#F8FAFC"`, bottom border `#E2E8F0`, `CornerRadius="12,12,0,0"`, `Padding="20,16"`):
  - Top row: `Factura N° 00289` (`18px`, `Bold`, `#0F172A`) + real status pill (`● Pendiente de Retiro` or `● Retiro Parcial`) + paid-date badge (`Pagado: 27/11/26 01:48`, `#F1F5F9`/`#475569`). Right side: highlighted total paid (`Bs.S 212,249.25`, `18px`, `Bold`, tabular, `#0F172A`) plus `$ 229.12` (`12.5px`, `#64748B`) in `Dual`.
  - Compact customer card (`#FFFFFF`, border `#E2E8F0`, `CornerRadius="10"`, `Padding="12,10"`, `Margin="0,12,0,0"`): circular initials avatar (`34x34px`, `#EDE9FE`/`#5B21B6`), full name (`SemiBold`, `#0F172A`), chips for `Cédula/RIF` (`V-1213132`) and `Teléfono` (`0412-2222222`) with a discreet icon button to copy the phone to the clipboard.
  - Delivery progress (`Margin="0,12,0,0"`): label `Progreso de entrega` left, `0 de 179 unidades entregadas (0%)` in `SemiBold` right, horizontal bar (`Height="6"`, `CornerRadius="3"`, `#E2E8F0` track, `#6D28D9` or `#16A34A` fill).
- Product custody grid (`Height="*"`): clean virtualized `DataGrid` (`HeadersVisibility="Column"`, `GridLinesVisibility="Horizontal"`, `HorizontalGridLinesBrush="#F1F5F9"`, `RowHeight="48"`, `BorderThickness="0"`, thin `6px` scrollbar). Columns: `PRODUCTO` (`Width="*"`, `MinWidth="200"`, `SemiBold`, `#0F172A`, ellipsis with tooltip), `COMPRADO` (`Width="85"`, centered, tabular `#475569`), `ENTREGADO` (`Width="90"`, centered, tabular `#15803D` when `> 0` else `#94A3B8`), `PENDIENTE` (`Width="95"`, centered pill: `#FEF3C7`/`#B45309` `Bold` when `> 0`, `#DCFCE7`/`#15803D` `✓ Listo` when `== 0`), `P. UNIT.` (`Width="115"`, right, tabular `#64748B`, dynamic currency code), `SUBTOTAL` (`Width="125"`, right, tabular `SemiBold` `#0F172A`, dynamic currency code). The product column absorbs all slack; no dead gap.
- Fixed dispatch footer (`Background="#F8FAFC"`, top border `#E2E8F0`, `CornerRadius="0,0,12,12"`, `Padding="20,14"`): left dispatch summary (`"1 producto · 179 unidades pendientes por entregar"`, `12.5px`, `#64748B`); right with `10px` gap: secondary clean button (`#FFFFFF`, border `#CBD5E1`, `#334155`, hover `#F1F5F9`, `Height="40"`) and flat primary button (`#6D28D9`, hover `#5B21B6`, `Foreground="White"`, `Height="40"`, no heavy black shadow). Both existing commands keep their logic and gain unambiguous labels/tooltips: `ConfirmPickupCommand` opens the quantity dialog (blank quantities) and is labeled `Entrega Parcial`; `DeliverAllCommand` prefills all pending quantities then opens the same confirmation dialog and is labeled `Entregar Todo (100%)`. Preserve `PendingPickups_ConfirmButton` and `PendingPickups_DeliverAllButton`.

S7. Empty states: no custody registered shows a clean centered box icon `#CBD5E1` and title `"No hay retiros pendientes"`; a filter/search with no matches shows `"Sin resultados para tu búsqueda"` plus a `Limpiar filtros` button when applicable. The detail panel shows its own neutral empty state when no invoice is selected.

S8. Strict `Single`/`Dual` support: every secondary-currency equivalence (`$` line) is hidden in `Single` through `MonetaryProfilePresenter.Instance.IsSingle` + `DualOnlyVisibilityConverter`. DataGrid unit/subtotal headers use the active local currency display code.

S9. Constraints: no new external libraries, no dense black `DropShadowEffect`, no `#000000` lines, no deformed oval badges, no API contract changes, no pickup/delivery logic changes.

S10. ViewModel (no new endpoints): in-memory KPIs, text filter (invoice, customer, cedula/RIF, product), status filter (`Todas`/`Sin retirar`/`Parciales`), three sort modes, count labels, auto-selection, clear-search/clear-filters commands, and phone-copy request event following the existing Core-without-WPF pattern.

ViewModel surface consumed by the view (names are contractual):
- Collections/state: `Pickups`, `FilteredPickups`, `SelectedCustody`, `SelectedStatusFilter` (`PickupStatusFilter`), `SelectedSortOption` (`PickupSortOption` with `Label` + `Mode`), `SortOptions`, `SearchQuery`.
- KPIs: `ActiveCustodyCount`, `ActiveCustodySubtitle`, `UnitsToDeliver`, `UnitsToDeliverSubtitle`, `PartialPickupsCount`, `PartialPickupsValue`, `PartialPickupsSubtitle`, `CustodyValueBsS`, `CustodyValueUSD`.
- Filter labels/counts: `AllFilterLabel`, `NotPickedUpFilterLabel`, `PartialFilterLabel`, `VisiblePickupCountLabel`, `HasActiveFilters`, `IsListEmpty`, `EmptyStateTitle`, `EmptyStateDescription`.
- Commands/events: `ClearSearchCommand`, `ClearFiltersCommand`, `SetStatusFilterCommand(PickupStatusFilter)`, `CopyCustomerPhoneCommand`, `RequestClipboardCopy` (`Action<string>`).
- Client DTO additive helpers: `PendingUnits`, `PendingLineCount`, `PendingValueBsS`, `PendingValueUSD`, `InvoiceDisplay`, `InvoiceBadge`, `ShortStatusLabel`, `DetailStatusLabel`, `PendingUnitsLabel`, `HasLargePendingVolume`, `DateDisplay`, `AgeLabel`, `IsStale`, `PaymentDateDisplay`, `DeliveryProgressLabel`, `DispatchSummaryLabel`.

S11. Unit tests must cover the four KPI calculations, text search by invoice/customer/cedula/product, the three status filters, all sort modes, auto-selection after load/filter/completion, clear filters, and proportional custody value for partial deliveries. Existing delivery, dedupe, paging, retry, and note tests remain green.

S12. Validation: `dotnet build CommandCenter.slnx -c Release` with `0 warnings / 0 errors`; full .NET suite green; visual validation at 100% and 125% DPI for (1) no deformed oval pills, (2) no dead space between `PRODUCTO` and `CANT.`, and (3) instant right-panel refresh when changing invoices in the tray.

S13. Preserve the E2E UI contract: `PendingPickups_List`, `PendingPickups_SearchInput`, `PendingPickups_RefreshButton`, `PendingPickups_ConfirmButton`, and `PendingPickups_DeliverAllButton` remain discoverable; the modal flow stays unchanged.

Assumptions:
- A1. `VALOR EN CUSTODIA` is the proportional pending merchandise value: per item `UnitPriceBsS x PendingQuantity` (fallback `SubtotalBsS x PendingQuantity / Quantity`), capped at the invoice total; USD equivalent derives from `AppliedRate`, falling back to the effective USD total scaled by the pending-unit fraction.
- A2. High-volume highlight threshold is `50` pending units.
- A3. Stale custody means more than `7` days since the payment date in Venezuela local time.
- A4. KPI strip uses the full loaded collection; pill counts use the text-search subset; the visible list also applies status + sort.
- A5. New client DTO members are display-only computed properties; the server contract is untouched.
- A6. Phone copy is raised by the Core ViewModel and handled by the view (`Clipboard.SetText`) so `Desktop.Client.Core` keeps no WPF dependency.
- A7. `ConfirmPickupCommand` opens the dialog with quantities at zero; `DeliverAllCommand` pre-fills all pending quantities and then uses the same confirmation dialog.
- A8. Existing `DeliveryStatusLabel` (`Entrega Parcial`/`Pendiente de Retiro`) remains for compatibility; new short/detail labels are additive.

## Tasks

- [x] T1 (S10, S11, S13) - Client DTO display/aggregate helpers + ViewModel KPIs, filters, sorting, auto-selection, clear/copy commands + unit tests. Route: delegated writer (`gentle-ai-worker`). RED: compile-time missing members; GREEN: 47/47 focused tests. Commit `8b0c696`.
- [x] T2 (S1-S9, S13) - Rewrite `PendingPickupsView.xaml` and its code-behind with the enterprise header, KPI strip, filter bar, master-detail split, product grid, footer actions, empty states, and clipboard handling. Route: delegated writer + parent visual fixes. Build 0/0; focused suite 50/50; offscreen WPF harness at 96/125 DPI. Commit 1ccf75.
- [x] T3 (S12) - Release build, targeted + full test suite, visual/Dpi validation at 96/125 DPI (offscreen harness), fix regressions, final work-unit commit. Full main suite 4517/4579; the 62 non-green are the PostgreSQL lane gated by a missing connection and an OOM in the full lane run; the 3 custody PostgreSQL tests pass against an isolated cluster. Commit 1ccf75.

## Log

L1. User request (verbatim):

Actúa como un Principal WPF UI/UX & MVVM Architect. Vamos a ejecutar una reestructuración completa y mejora visual nivel Masterchip sobre la vista **Retiros Pendientes (Mercancía en Custodia)** (`PendingPickupsView.xaml` / `CustodyView.xaml` o vista equivalente y su ViewModel).

**Objetivo central:** Eliminar la sensación de pantalla vacía y los espacios muertos de las tarjetas estiradas al 100% del ancho, transformando la vista en un **Centro de Despacho Enterprise** con franja de KPIs operativos, barra de filtros completa y un espacio de trabajo dividido (*Split Master-Detail* `38% / 62%`).

**Reglas inquebrantables:**
- Respeta estrictamente la paleta institucional del POS (`#F8FAFC` fondo base, `#FFFFFF` tarjetas, `#E2E8F0` bordes de tarjeta, `#CBD5E1` bordes de inputs, `#6D28D9` acento primario, `#5B21B6` hover, `#EDE9FE`/`#F5F3FF` acento suave, `#0F172A` texto primario, `#64748B` texto secundario).
- **Prohibido:** librerías externas nuevas, sombras negras pesadas (`DropShadowEffect` densos), líneas `#000000`, badges ovalados deformes y romper contratos de API o lógica de retiro existente.
- Soporta estrictamente régimen `Single` y `Dual` (ocultando montos equivalentes en moneda secundaria cuando el comercio opere en `Single`).

### 1. Cabecera Unificada + Franja de 4 Tarjetas KPI Operativas
- **Cabecera:**
  * Envuelve el ícono de custodia (caja/paquete) en el badge estándar del sistema (`42×42px`, `CornerRadius="10"`, `Background="#EDE9FE"`, ícono `#6D28D9`).
  * Título `Retiros Pendientes (Mercancía en Custodia)` (`22px`, `Bold`, `#0F172A`) + subtítulo `Mercancía pagada al 100% pendiente de entrega física al cliente.` (`13px`, `#64748B`).
  * Botones derechos `Refrescar` y menú alineados (`Height="38"`, `CornerRadius="8"`, `#FFFFFF`, borde `#CBD5E1`, `#334155`, hover `#F1F5F9`).
- **Franja de 4 KPIs (calculados en memoria desde la colección cargada en el ViewModel, sin nuevos endpoints):**
  * `Grid` de 4 columnas iguales (`Margin="0,12,0,12"`, separación `12px`). Estilo de tarjeta blanca (`#FFFFFF`, borde `#E2E8F0`, borde izquierdo de acento `4px`, `CornerRadius="12"`, `Padding="16,12"`, sombra mínima `Opacity="0.03"`):
    1. **`CUSTODIAS ACTIVAS`** (acento `#6D28D9`): Total de facturas pendientes (`22px`, `Bold`, `#0F172A`) + subtítulo con cuántos clientes distintos tienen retiros abiertos.
    2. **`UNIDADES POR ENTREGAR`** (acento `#D97706`): Suma de todas las cantidades pendientes (ej. `181 uds.`) + subtítulo indicando líneas de producto por despachar.
    3. **`RETIROS PARCIALES`** (acento `#0284C7`): Cantidad de facturas que ya tienen `Entregado > 0` pero aún tienen saldo pendiente (o `"0 parciales"` en tono neutro si no hay).
    4. **`VALOR EN CUSTODIA`** (acento `#16A34A`): Suma del valor pagado pendiente/total en `Bs.S` (`20px`, `Bold`, fuente tabular) + equivalente en `$` debajo (oculto en régimen `Single`).

### 2. Barra de Herramientas y Filtros (Fin del 80% Vacío)
- Encierra los controles en una tarjeta blanca (`#FFFFFF`, borde `#E2E8F0`, `CornerRadius="12"`, `Padding="14,10"`, `Margin="0,0,0,12"`):
  * **Izquierda — Buscador amplio (`MinWidth="340"`, `Width="*"`, `MaxWidth="460"`):** `Height="38"`, `CornerRadius="8"`, `BorderBrush="#CBD5E1"` (foco `#6D28D9`), placeholder `"Buscar por N° factura, cliente, cédula/RIF o producto..."` con botón interno `×` para limpiar texto.
  * **Centro — Filtro Segmentado de Estado:** Contenedor `#F1F5F9` (`CornerRadius="8"`, `Padding="3"`) con 3 botones pill clicables que filtran la bandeja localmente:
    - `Todas (N)`
    - `Sin retirar (X)` (`Entregado == 0`)
    - `Parciales (Y)` (`Entregado > 0 && Pendiente > 0`)
  * **Derecha — Selector de Orden (`ComboBox` limpio DPI-safe `Height="38"`, `MinWidth="185"`):**
    - `Más recientes primero` (por defecto)
    - `Más antiguas primero` (ideal para priorizar mercancía rezagada)
    - `Mayor cantidad pendiente`

### 3. Reestructuración del Workspace Principal: Split Master-Detail (`38%` / `62%`, `Height="*"`)
Reemplaza la lista única de tarjetas gigantes por un `Grid` de dos columnas (`Width="3.8*"` con `MinWidth="360"`, separación `14px`, `Width="6.2*"`) ocupando todo el alto restante de la ventana (`Height="*"`):

#### A. Panel Izquierdo (`38%`) — Bandeja de Custodias (Alta Densidad Escaneable)
- Contenedor blanco (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`).
- **Cabecera fija (`Height="44"`, `#F8FAFC`, borde inferior `#E2E8F0`, `CornerRadius="12,12,0,0"`, `Padding="16,0"`):**
  * Título `Facturas en custodia` (`13.5px`, `SemiBold`, `#0F172A`) + contador pill a la derecha (`N mostradas`).
- **Lista virtualizada (`ListBox` con `VirtualizingStackPanel.IsVirtualizing="True"`, `ScrollBar` delgado `Width="6"`):**
  * **Auto-selección inteligente:** Al cargar o filtrar, selecciona automáticamente la primera factura disponible si `SelectedCustody` es nulo o quedó fuera del filtro.
  * **Diseño de cada ítem compacto (`Padding="14,12"`, borde inferior `1px` `#F1F5F9`, hover `#F8FAFC`, seleccionado `#F5F3FF` con barra lateral izquierda `3px` en `#6D28D9`):**
    - **Fila 1:** Badge `Factura #00290` (`#EDE9FE`/`#5B21B6`, `CornerRadius="6"`, `Padding="8,2"`, `SemiBold`) + **Píldora de estado bien proporcionada** (`CornerRadius="999"`, `Padding="8,2"` — **corrige la deformación ovalada actual**: `● Sin retirar` en `#FEF3C7`/`#B45309` o `● Parcial` en `#DBEAFE`/`#1D4ED8`) + a la derecha, monto principal (`Bs.S 212,249.25`, `13px`, `Bold`, fuente tabular).
    - **Fila 2:** Nombre del cliente (`13px`, `SemiBold`, `#0F172A`, `TextTrimming="CharacterEllipsis"` con `ToolTip`) + cédula/RIF tenue (`#64748B`).
    - **Fila 3 (Metadatos operativos):**
      * Izquierda: **Pill de volumen** (`179 uds. pendientes` en `#F1F5F9`/`#334155`, resaltado en `#FFFBEB`/`#B45309` si tiene muchas unidades) + fecha/antigüedad (`27/11/26 · Hoy` o `Hace X días`, cambiando a tono ámbar si supera 7 días en custodia).
      * Derecha: equivalente `$ 229.12` (oculto en `Single`).
    - **Fila 4 (Progreso sutil):** Mini barra de progreso (`Height="4"`, `#E2E8F0` fondo / `#16A34A` avance) que refleja visualmente `Entregado / Total`.

#### B. Panel Derecho (`62%`) — Panel de Despacho Activo (Detalle de la Factura Seleccionada)
- Contenedor blanco (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`) estructurado internamente en 3 filas (`Auto`, `*`, `Auto`):
- **1. Encabezado del Despacho (`Background="#F8FAFC"`, borde inferior `#E2E8F0`, `CornerRadius="12,12,0,0"`, `Padding="20,16"`):**
  * **Fila superior:**
    - Izquierda: Título `Factura N° 00289` (`18px`, `Bold`, `#0F172A`) + píldora de estado real (`● Pendiente de Retiro` o `● Retiro Parcial`) + badge de fecha de pago (`Pagado: 27/11/26 01:48` en `#F1F5F9`/`#475569`).
    - Derecha: Total pagado destacado (`Bs.S 212,249.25` en `18px`, `Bold`, fuente tabular `#0F172A` + `$ 229.12` en `12.5px` `#64748B` en régimen `Dual`).
  * **Tarjeta compacta de Ficha del Cliente (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `CornerRadius="10"`, `Padding="12,10"`, `Margin="0,12,0,0"`):**
    - Avatar circular de iniciales (`34×34px`, `#EDE9FE`/`#5B21B6`) + Nombre completo (`SemiBold`, `#0F172A`) + chips de `Cédula/RIF` (`V-1213132`) y `Teléfono` (`0412-2222222`, con botón iconográfico discreto para copiar teléfono al portapapeles y contactar al cliente fácilmente).
  * **Barra de Progreso de Entrega (`Margin="0,12,0,0"`):**
    - Etiqueta `Progreso de entrega` a la izquierda + `0 de 179 unidades entregadas (0%)` en `SemiBold` a la derecha + barra horizontal (`Height="6"`, `CornerRadius="3"`, `#E2E8F0` / `#6D28D9` o `#16A34A`).
- **2. Grilla de Productos en Custodia (`Height="*"`, `Padding="0"`):**
  * `DataGrid` virtualizado limpio (`HeadersVisibility="Column"`, `GridLinesVisibility="Horizontal"`, `HorizontalGridLinesBrush="#F1F5F9"`, `RowHeight="48"`, `BorderThickness="0"`, `ScrollBar` delgado `Width="6"`).
  * Columnas perfectamente proporcionadas (sin el abismo vacío de 900px):
    1. `PRODUCTO` (`Width="*"`, `MinWidth="200"`, `SemiBold`, `#0F172A`, `TextTrimming="CharacterEllipsis"` con `ToolTip`).
    2. `COMPRADO` (`Width="85"`, centrado, fuente tabular `#475569`, ej. `179`).
    3. `ENTREGADO` (`Width="90"`, centrado, fuente tabular `#15803D` si `> 0` o `#94A3B8` si `0`).
    4. `PENDIENTE` (`Width="95"`, centrado): píldora destacada (`#FEF3C7`/`#B45309` `Bold` cuando `> 0`; `#DCFCE7`/`#15803D` `✓ Listo` cuando `== 0`).
    5. `P. UNIT.` (`Width="115"`, alineado a la derecha, fuente tabular `#64748B`, código de moneda dinámico).
    6. `SUBTOTAL` (`Width="125"`, alineado a la derecha, fuente tabular `SemiBold` `#0F172A`, código de moneda dinámico).
- **3. Pie Fijo de Acciones de Despacho (`Background="#F8FAFC"`, borde superior `#E2E8F0`, `CornerRadius="0,0,12,12"`, `Padding="20,14"`):**
  * **Conserva ambos comandos existentes del ViewModel sin romper su lógica**, pero aclara visualmente su jerarquía y propósito:
    - **Izquierda:** Resumen breve del despacho (`"1 producto · 179 unidades pendientes por entregar"` en `12.5px`, `#64748B`).
    - **Derecha (separación `10px`):**
      * Inspecciona en el ViewModel qué ejecuta exactamente cada uno de los dos comandos actuales (`Retiro Completo` y `Confirmar Retiro / Entregar`) y asigna etiquetas sin ambigüedad que reflejen su acción real:
        - Si uno abre el diálogo de entrega parcial/personalizada y el otro despacha el 100% directamente: estiliza el secundario como botón limpio (`#FFFFFF`, borde `#CBD5E1`, `#334155`, hover `#F1F5F9`, `Height="40"`, ej. `Entrega Parcial` o `Retiro Parcial`) y el primario como botón plano `#6D28D9` (hover `#5B21B6`, `Foreground="White"`, `Height="40"`, sin sombra negra pesada, ej. `Entregar Todo (100%)` o `Confirmar Entrega`).
        - Añade `ToolTip` claros a ambos botones explicando exactamente qué hará cada uno al hacer clic.
- **Estado Vacío Inteligente:**
  * Si no hay ninguna custodia registrada (o el filtro no devuelve coincidencias), muestra un estado vacío limpio centrado con ícono de caja `#CBD5E1`, título `"No hay retiros pendientes"` (o `"Sin resultados para tu búsqueda"`) y botón `Limpiar filtros` cuando aplique.

### 4. Validación y Pruebas
- Agrega/actualiza pruebas unitarias de ViewModel cubriendo: cálculo de los 4 KPIs, filtro por texto (factura/cliente/RIF/producto), filtros `Todas` / `Sin retirar` / `Parciales`, ordenamiento y auto-selección del ítem activo tras completar un retiro.
- Compila con `dotnet build CommandCenter.slnx -c Release` (`0 warnings / 0 errors`), confirma toda la suite `.NET` en verde y valida visualmente a 100% y 125% de escala DPI que: (1) no existan píldoras ovaladas deformes, (2) no queden espacios muertos entre `PRODUCTO` y `CANT.`, y (3) el panel derecho actualice instantáneamente al cambiar de factura en la bandeja izquierda.

L2. Baseline: `dotnet build CommandCenter.slnx -c Release` green (0 warnings / 0 errors); `gentle-ai review mode status` reports off (global off, clone-local off). Pre-existing uncommitted work in the tree is out of scope and must not be staged.

L3. Assumptions recorded in A1-A8 (proportional custody value, 50-unit highlight, 7-day stale threshold, KPI vs filter scopes, display-only DTO helpers, clipboard event, command semantics, additive labels).

L4. T1 implemented by `gentle-ai-worker`: 17 client DTO display/aggregate helpers, filtered projection + KPIs + filters/sorts/auto-selection + clipboard event, 25 new facts + 1 theory. RED was compile-time missing surface; GREEN 47/47 focused tests. Release solution build 0 warnings / 0 errors. Parent spot fix: `DaysInCustody` now uses `TimeZoneHelper.GetVenezuelaDate()` instead of machine-local `DateTime.Today`.

L5. Commit `8b0c696` contains only the three T1 files. The pre-existing unstaged `CancelSaleAsync` interface line was excluded from the commit and restored to the working tree, remaining unrelated WIP. The commit used `--no-verify`; inspection showed this checkout has no `pre-commit` hook installed (only `pre-push`), so no hook was bypassed. Future work units omit the flag.

L6. T2 implemented by `gentle-ai-worker`: full enterprise rewrite of `PendingPickupsView.xaml` + minimal clipboard-only code-behind (no CollectionView). Local styles only; `SettingsStyles.xaml` merged; all AutomationIds preserved. Build 0 warnings / 0 errors and focused suite 50/50 after parent fixes.

L7. Offscreen WPF harness (temporary, outside the repo) rendered the real view with seeded pickups at 1280x820 and 1600x900 (96 DPI), 1280x820 at 125% DPI, 1093x700 at 125% DPI, 1024x720 default, Dual and Single, partial delivery, filtered-empty, and no-custody states. Visual review found and fixed: sort ComboBox showing the record `ToString()` (ItemTemplate + label `ToString`), list `$` equivalent using pending value instead of the paid total, DataGrid header compression at narrow widths (horizontal scroll + equal `MinWidth`, sorting/resizing disabled, tighter center header padding), empty phone chip (additive `HasCustomerPhone`/`CustomerPhoneDisplay` + hidden copy button), truncated header title, and filter-pill hover contrast. Final renders show capsule pills (no deformed ovals), no dead gap between PRODUCTO and the numeric columns, a full-width product column, and instant detail updates when selecting a different tray row.

L8. Final evidence: `dotnet build CommandCenter.slnx -c Release` 0 warnings / 0 errors; `PendingPickupsViewModelTests` 50/50; full `CommandCenter.Tests` 4517 passed / 62 failed / 4579 total. Every one of the 62 failures is the environment-gated real-PostgreSQL lane (`PostgresTestLane.RequireResolution`: no `TEST_POSTGRES_CONNECTION`). Running that lane against a temporary isolated PostgreSQL 18 cluster passed the 3 custody-specific tests; the full 62-test lane aborted with a testhost out-of-memory on this machine, unrelated to the feature. The full-stack FlaUI custody E2E was not run because its fixture is gated off without the dedicated connection/CI environment; the automation ids and modal flow it depends on are preserved.

L9. Concurrent sessions committed unrelated work on this branch while the feature was in progress (`b9a1258`, `cc29751`, `e411933`). This work unit stages only `PendingPickupsView.xaml`, `PendingPickupsView.xaml.cs`, the additive client DTO/VM presentation members, the phone-display test, and this ODD document; unrelated working-tree changes remain untouched.
