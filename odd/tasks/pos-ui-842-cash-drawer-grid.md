# POS-UI-842 - Modernizacion de la grilla de Movimientos de Caja (Fisicos)

Rama: `V0.15`. Entrega: work units locales; push/PR a decision del mantenedor.
Runner: `dotnet build CommandCenter.slnx -c Release` + `dotnet test` (filtro caja/cash-drawer + suite no-PG).

## Specs

- S1. Layout: la ventana conserva sus filas superiores y la tarjeta de movimientos ocupa el alto
  restante (`RowDefinition Height="*"`). El bloque "ULTIMOS INGRESOS RECIBIDOS" pasa a una barra
  compacta colapsable (default colapsada) para liberar altura vertical.
- S2. DataGrid: sin `RowHeader` gris (`HeadersVisibility="Column"`), sin lineas verticales, con
  separadores horizontales tenues `#F1F5F9`; virtualizacion Recycling intacta.
- S3. Envoltorio: grilla y paginador dentro de una tarjeta blanca con `CornerRadius` 12 y borde
  `#E2E8F0`, replicando la estetica de las tarjetas de KPI.
- S4. Cabecera de grilla: fondo `#F8FAFC`, tipografia seminegrita `#64748B` de 12 px, padding
  interno generoso y un unico borde divisor inferior.
- S5. Columnas: `Fecha/Hora`, `Tipo`, `Origen`, `Monto (Bs.S)`, `USD` y `Tasa` con ancho fijo;
  `Factura / Detalle` es la unica columna `Width="*"`. Importes y tasa alineados a la derecha con
  numeros tabulares (`Typography.NumeralAlignment="Tabular"`).
- S6. Badges: `Ingreso` verde pastel/texto verde oscuro; `Egreso` coral claro/carmesi oscuro;
  `Adelanto` (Source `CashAdvance`) y cualquier otro tipo en azul tenue/texto azul oscuro.
- S7. Fecha/Hora: fecha principal en una linea y hora secundaria debajo con menor opacidad,
  margen vertical comodo por celda.
- S8. Barra superior de la tarjeta: buscador compacto con placeholder que filtra por numero de
  factura o detalle, y chips `Todos`, `Ingresos`, `Egresos`. El filtro se aplica en memoria sobre
  el historial ya cargado, sin llamadas nuevas al backend.
- S9. Paginador integrado en la base de la tarjeta: conserva `Mostrando X a Y`, `Página X de Y` y
  los comandos `PreviousPageCommand`/`NextPageCommand`. El total y las paginas reflejan el
  conjunto filtrado; cambiar filtro o busqueda vuelve a la pagina 1.
- S10. Alcance: solo `CashDrawerView.xaml` (recursos locales) y `CashDrawerViewModel.cs`
  (busqueda/filtro/estado de alternancia). Sin cambios de backend, sin librerias externas de UI,
  build Release 0 errores/0 warnings y suite de caja al 100%.
- S11. Altura minima de la grilla: 8 filas visibles por defecto; si la pagina contiene mas de 8
  movimientos, se suman 2.5 filas adicionales (10.5 visibles) antes del scroll interno. El alto
  de fila (48) y de cabecera (44) se exponen desde el ViewModel para que XAML y calculo no
  dupliquen constantes.

## Tasks

- WU1 | S8/S9 | ViewModel: `SearchText`, `MovementTypeFilter`, filtro en memoria, reset de pagina y
  tests RED->GREEN | inline | commit e997390
- WU2 | S1-S7/S9 | XAML: layout flexible, tarjeta, DataGrid estilizado, badges, columnas y
  paginador integrado | inline | commit e997390
- WU3 | S10 | Build Release, tests de caja y suite no-PG | inline | commit e997390
- WU4 | S11 | Altura minima de grilla 8 filas + 2.5 adicionales con mas de 8 movimientos |
  inline | commit 5b455ee

## Log

- L1 (2026-11-09): pedido verbatim del mantenedor (JIRA POS-UI-842):

  > ### [JIRA] TAREA TÉCNICA / HISTORIA DE USUARIO
  >
  > **Clave del Ticket:** `POS-UI-842`
  >
  > **Título (Summary):** `[WPF][UI/UX] Modernización visual y optimización de layout de la grilla de Movimientos de Caja (Físicos)`
  >
  > **Componente:** `Desktop.Client (WPF)`
  >
  > **Módulo:** `CashDrawer / Register`
  >
  > **Prioridad:** Media / Alta (P2)
  >
  > **Asignado a:** Agente de Desarrollo / Frontend WPF
  >
  > ---
  >
  > #### 1. Descripción y Contexto del Problema
  >
  > En la vista actual de **Caja de Tienda (`REGISTER / CASH DRAWER`)** , la sección inferior de **Movimientos de Caja (Físicos)** presenta una brecha visual y de usabilidad significativa frente a las tarjetas superiores modernizadas:
  >
  > * **Viewport vertical asfixiado:** La tabla queda confinada a un alto restringido que solo permite visualizar una fila completa y dos filas cortadas, obligando a un desplazamiento vertical forzado en una sección que contiene 171 registros distribuidos en 7 páginas.
  > * **Persistencia de componentes nativos sin estilizar:** Se aprecia la columna lateral gris predeterminada de WPF (`RowHeader`) con el selector de flecha negra a la izquierda de la fecha.
  > * **Sobrecarga visual de bordes:** Cuadrícula con líneas duras negras tanto verticales como horizontales y un marco rígido que rompe con la paleta limpia del resto del panel.
  > * **Redundancia espacial:** El contenedor intermedio de **"Últimos ingresos recibidos"** consume aproximadamente 160 px de alto vertical para repetir facturas que ya figuran en las primeras líneas de la propia tabla.
  >
  > ---
  >
  > #### 2. Objetivo de la Tarea
  >
  > Reestructurar el maquetado XAML de la sección inferior de `CashDrawerView` para otorgarle el protagonismo y la altura vertical que requiere el libro diario de caja, modernizando el aspecto del `DataGrid` (encabezados, bordes, badges y alineaciones) sin incorporar componentes de terceros ni alterar la lógica transaccional existente.
  >
  > ---
  >
  > #### 3. Especificaciones de Diseño y Criterios de Aceptación (Definition of Done)
  >
  > ##### A. Optimización de Espacio y Jerarquía de Layout
  >
  > * **Liberación de altura:** Redefinir la distribución de filas del contenedor principal para que la sección de la tabla tome un alto flexible (`RowDefinition Height="*"`) y aproveche todo el espacio libre de la ventana.
  > * **Compactación del bloque intermedio:** Reducir la huella vertical del contenedor de "Últimos ingresos recibidos" (haciéndolo colapsable mediante un botón de alternancia o reemplazándolo por una barra resumen de accesos rápidos) para garantizar que la tabla muestre de forma visible entre 8 y 10 registros por página sin recorte.
  >
  > ##### B. Limpieza y Estilizado del DataGrid
  >
  > * **Eliminación de elementos heredados:**
  >   * Ocultar por completo la cabecera de fila gris (`RowHeader`) configurando la visibilidad de cabeceras únicamente en columnas.
  >   * Suprimir todas las líneas verticales duras de la cuadrícula.
  >   * Reemplazar las líneas horizontales negras por un separador tenue de bajo contraste (gris claro `#F1F5F9`).
  > * **Envoltorio moderno:** Encapsular la grilla y su paginador dentro de una tarjeta con fondo blanco, esquinas redondeadas (`CornerRadius` de 8 a 12 px) y borde gris suave (`#E2E8F0`), replicando la estética de las tarjetas de KPIs.
  > * **Cabecera de grilla estilizada:**
  >   * Fondo gris tenue (`#F8FAFC`) con tipografía seminegrita en tono pizarra (`#64748B`), tamaño 12 px y padding interno generoso.
  >   * Borde divisor inferior único para separar los títulos de las filas de datos.
  >
  > ##### C. Tratamiento de Columnas, Tipografía y Badges
  >
  > * **Alineación numérica estricta:** Todas las columnas de importes y tasas (`Bs.S`, `USD`, `Tasa`) deben tener alineación obligatoria hacia la derecha y fuentes con espaciado uniforme para preservar la columna decimal.
  > * **Pills de Tipo de Movimiento:**
  >   * Reemplazar el texto simple por un contenedor tipo píldora (*badge*) centrado o con alineación fija:
  >   * **Ingreso:** Fondo verde pastel suave con texto verde oscuro de alto contraste.
  >   * **Egreso / Salida:** Fondo rojo o coral claro con texto carmesí oscuro.
  >   * **Adelanto / Otros:** Fondo azul tenue con texto azul oscuro.
  > * **Fecha y Hora:** Conservar la fecha principal en una línea y la hora secundaria debajo con menor opacidad, asegurando un margen vertical cómodo por celda.
  > * **Distribución de anchos de columna:** Asignar anchos fijos a `Fecha/Hora`, `Tipo`, `Origen`, `Montos` y `Tasa`, permitiendo que la columna `Factura / Detalle` sea la única que absorba el ancho dinámico sobrante (`Width="*"`).
  >
  > ##### D. Barra Superior de Control de la Grilla
  >
  > * Incorporar en la cabecera de la tarjeta de movimientos (a la derecha del título `Movimientos de Caja (Físicos)`):
  >   * Un cuadro de búsqueda compacto con texto de ayuda (*placeholder*) para filtrar por número de factura o detalle sin salir de la vista.
  >   * Opcional: Selector rápido tipo *chips* o pestañas sutiles para filtrar entre: `Todos`, `Ingresos`, `Egresos`.
  >
  > ##### E. Paginador Integrado
  >
  > * El paginador inferior (`Mostrando X a Y`, indicador `Página X de Y` y botones `Anterior`/`Siguiente`) debe quedar integrado en la base de la tarjeta, manteniendo los comandos de navegación existentes en el ViewModel.
  >
  > ---
  >
  > #### 4. Instrucciones Directas para el Agente
  >
  > 1. **Restricción de Código e Integridad:**
  >    * No modifiques controladores del backend, endpoints de la API ni la estructura de datos que alimenta `CashMovements`.
  >    * Todo el trabajo debe realizarse exclusivamente sobre el archivo de interfaz `CashDrawerView.xaml` (y su `Styles.xaml` o recursos de recursos locales) y, de ser necesario para el filtrado/búsqueda, en propiedades del `CashDrawerViewModel.cs`.
  > 2. **Cero dependencias externas:**
  >    * Utiliza únicamente primitivas y estilos nativos de WPF (`Border`, `DataGrid`, `Grid`, `DataTemplate`, `Style`, `StaticResource`). No agregues librerías externas de UI.
  > 3. **Validación:**
  >    * Asegura que el proyecto compile limpiamente con `dotnet build` en configuración Release sin advertencias nuevas.
  >    * Verifica que las pruebas unitarias y de integración de la sesión de caja sigan pasando al 100%.

- L2 (2026-11-09): Exploracion. `CashDrawerView.xaml` tiene 700 lineas; el `DataGrid` de
  movimientos usa `PerformantDataGrid` sin estilos locales y el historial se pagina en memoria
  (`_allPhysicalTransactions`, 25 por pagina) dentro de `CashDrawerViewModel`. No existe
  `Styles.xaml`; los estilos de tarjeta/encabezado se replican localmente como en
  `SalesHistoryView.xaml`. Se crea el documento antes del primer write. Mirror en Engram
  pendiente por degradacion del proveedor.

- L3 (2026-11-09): **WU1 COMPLETADA**. Tests RED observados por API faltante
  (`CashDrawerMovementFilterTests`); luego GREEN 8/8. `CashDrawerViewModel` agrega
  `SearchText`, `MovementTypeFilter` (`All/Income/Expense`), `IsFilter*`, `HasActiveFilter`,
  `IsRecentIncomesExpanded`, `SetMovementTypeFilterCommand` y `ToggleRecentIncomesCommand`.
  El filtro compone tipo + busqueda (factura o detalle, case-insensitive, trim) sobre el
  historial cargado; `TotalPhysicalTransactions`, `TotalPages` y `PaginationSummary` reflejan
  el conjunto filtrado y cada cambio vuelve a pagina 1. El resumen vacio distingue filtro
  aplicado de historial vacio. Sin cambios de backend ni del contrato de datos.

- L4 (2026-11-09): **WU2 COMPLETADA**. `CashDrawerView.xaml` reemplaza la seccion inferior por
  una tarjeta blanca `CornerRadius=12`/borde `#E2E8F0` con barra de control (titulo, contador,
  chips Todos/Ingresos/Egresos y buscador), `DataGrid` sin `RowHeader` ni lineas verticales,
  separadores `#F1F5F9`, cabecera `#F8FAFC`/`#64748B` 12 px y columnas con anchos fijos
  (Factura/Detalle `*`). Importes y tasa alineados a la derecha con numeros tabulares; badges
  verde/coral/azul para Ingreso/Egreso/Adelanto-Otros; fecha y hora apiladas. El bloque de
  ultimos ingresos queda como barra resumen colapsable (default colapsada) y el paginador se
  integra en la base de la tarjeta conservando `PreviousPageCommand`/`NextPageCommand`.

- L5 (2026-11-09): **WU3 VERIFICADA**. `dotnet build CommandCenter.slnx -c Release`: 0
  advertencias, 0 errores. Tests focalizados de caja/filtro/paginacion: 14/14. Suite completa
  `CommandCenter.Tests`: 4164 superados, 53 fallos ambientales del carril PostgreSQL por no
  tener `TEST_POSTGRES_CONNECTION` (todos los bloques de error son
  `PostgresTestLane.RequireResolution`; ninguno pertenece a este cambio). Queda registrar el
  ANEXO y el commit del work unit.

- L6 (2026-11-09): Work unit `e997390` (`feat(pos-ui-842)`). ANEXO LX registrado en
  `docs/reporte.txt`. Mirror en Engram guardado como observacion `858` del proyecto
  `proyecto_pos_estable`.

- L7 (2026-11-09): pedido verbatim del mantenedor:

  > 2.5 filas adicionales para vidsualizacion el alto por defecto que contiene las filas de
  > movimientos, para termitir la visualizacion simltanea de mas filas

  Interpretacion confirmada: la base son 8 filas; si la pagina actual tiene mas de 8, se suman
  2.5 filas (10.5 visibles). Se agrega S11/WU4.

- L8 (2026-11-09): **WU4 COMPLETADA**. `CashDrawerViewModel` expone `MovementGridRowHeight`
  (48), `MovementGridColumnHeaderHeight` (44) y `MovementsGridMinHeight`: 428 px para 8 filas
  (44 + 8x48) y 548 px para 10.5 filas (44 + 10.5x48); se notifica al cambiar la pagina o el
  filtro. El XAML bindea `RowHeight`, `ColumnHeaderHeight` y `MinHeight` a esas propiedades para
  no duplicar constantes. Para que el minimo no recorte el paginador en ventanas bajas, la vista
  completa queda dentro de un `ScrollViewer` con `Padding=24`; el Grid interno usa
  `MinHeight=ViewportHeight`, de modo que en pantallas grandes la fila `*` sigue absorbiendo el
  espacio libre y solo aparece scroll de pagina cuando el contenido no entra. El overlay de
  apertura se movio fuera del ScrollViewer para seguir cubriendo todo el viewport. Tests nuevos
  de altura minima y paginacion: 10/10 en `CashDrawerMovementFilterTests`; focalizados de caja
  16/16; build Release 0/0. Commit `5b455ee`.

- L9 (2026-11-09): Ajuste posterior reportado por el mantenedor: dentro de la caja de
  movimientos no se podia scrollear porque el `MinHeight` dejaba que la grilla se estirara al
  contenido dentro del ScrollViewer de pagina. Se agrega `MaxHeight` igual a
  `MovementsGridMinHeight`, `VerticalAlignment="Top"` y
  `ScrollViewer.VerticalScrollBarVisibility="Auto"`: la caja queda fija en 8 filas (o 10.5 con
  mas de 8) y el resto de la pagina se navega con el scrollbar interno de la grilla. Build
  Release 0/0; focalizados de caja 16/16.

