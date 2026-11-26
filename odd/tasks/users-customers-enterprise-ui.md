# Gestión de Usuarios y Clientes — mejora visual Enterprise

Objetivo: rediseñar `UsersManagementView.xaml` y sus estilos asociados con cabecera, segmented control, tarjetas de directorio, tabla e inspector, preservando bindings, comandos y validaciones existentes; agregar búsqueda/filtros de Usuarios y Clientes, avatares, badges, resaltado de selección, estado, `Single` de presentación y paginación unificada.
Branch: `V0.15` (rama actual; push/PR a decisión del mantenedor).
Delivery: work units locales; UI WPF + filtro de estado de clientes server-side + tests.
Test runner: `dotnet build CommandCenter.slnx -c Release`; `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; visual harness temporal 96/120 DPI fuera del repo.

## Specs

- S1. Paleta institucional: `#F8FAFC`, `#FFFFFF`, `#E2E8F0`, acento `#6D28D9`. Sin paquetes externos. Conservar bindings, comandos, validaciones y `AutomationProperties.AutomationId` existentes.
- S2. Cabecera: contenedor 42x42 `#EDE9FE`/`CornerRadius=10` con ícono de usuarios `#6D28D9`; título `Gestión de Usuarios y Clientes` 22 Bold `#0F172A`; subtítulo `Administra accesos, roles del personal y el directorio de clientes.` 13 `#64748B`; engranaje en botón secundario formal arriba a la derecha (`#FFFFFF`, borde `#E2E8F0`, `CornerRadius=8`, `Padding=12,8`) con el comando de drawer existente.
- S3. Segmented control: contenedor `#F1F5F9`, `CornerRadius=10`, `Padding=4`; activa `#FFFFFF` con `CornerRadius=8`, texto SemiBold y contador pill del total; inactiva transparente `#475569` con hover sutil. Etiquetas `Usuarios y Cajeros` + `{TotalUserCount}` y `Gestión de Clientes` + `{CustomerViewModel.TotalCount}`.
- S4. Tarjeta izquierda: `#FFFFFF`, borde `#E2E8F0`, `CornerRadius=12`, `Height=*`; contiene toolbar, tabla y footer. Toolbar simétrica en ambas pestañas: buscador 40px con borde `#CBD5E1` y `CornerRadius=8`; filtro rápido; botón `Refrescar` secundario. Usuarios: búsqueda `UserSearchQuery` por usuario/nombre y filtro `Todos los roles` / `Administrador` / `Gerente` / `Cajero`. Clientes: `SearchQuery` existente, filtro `Todos` / `Activos` / `Inactivos` y `Refrescar` existente.
- S5. Tabla: `HeadersVisibility=Column`, `GridLinesVisibility=Horizontal`, líneas `#F1F5F9`, `RowHeight=50`, borde 0, scrollbar 6px. Avatar 32x32 con iniciales (usuarios `#EDE9FE`/`#5B21B6`; clientes `#F1F5F9`/`#334155`). Anchos sin recorte: Usuario/Nombre `MinWidth=160`, Cédula/RIF `MinWidth=130` tabular, Teléfono cliente `MinWidth=140` tabular. Badges: Admin `#EDE9FE`/`#5B21B6`, Gerente `#DBEAFE`/`#1D4ED8`, Cajero `#FEF3C7`/`#B45309`; Estado `● Activo` `#DCFCE7`/`#15803D`, `● Inactivo` `#F1F5F9`/`#64748B`. Fila seleccionada `#F5F3FF` con borde izquierdo 3px `#6D28D9`.
- S6. Footer unificado en ambas tarjetas: borde superior `#F1F5F9`, `Background=#FAFAFA`, `CornerRadius=0,0,12,12`, contador/estado y paginador de clientes.
- S7. Inspector derecho: columna 390px, tarjeta blanca borde `#E2E8F0`, `CornerRadius=12`, misma altura que la izquierda. Cabecera `#F8FAFC`, borde inferior `#E2E8F0`, `CornerRadius=12,12,0,0`, `Padding=20,14`, `DockPanel`/`Grid` `*` / `Auto`: título dinámico `Nuevo Usuario` / `Editar Usuario` o `Nuevo Cliente` / `Editar Cliente` 15 Bold `#0F172A`, subtítulo/badge de modo, y `+ Nuevo` compacto `Height=32`, `Padding=10,6`, `#F5F3FF`, borde `#DDD6FE`, texto `#6D28D9`, sin superposición.
- S8. Formulario: etiquetas arriba 12 SemiBold `#334155` `Margin=0,0,0,6`; inputs 40px `CornerRadius=8`, reposo `#F8FAFC`/borde `#CBD5E1`, foco `#FFFFFF`/borde `#6D28D9` 1.5px. Placeholder de contraseña `Opcional (por defecto: usuario)` con margen derecho para el ojo; al editar, `Dejar en blanco para conservar la actual`. Contador de Nombre de cliente único a la derecha de la etiqueta, sin `0 / 50` dentro del input; ayuda `Máximo 50 caracteres para reportes y facturas` 11 `#64748B`. ComboBox de rol con etiquetas `Cajero`, `Gerente`, `Administrador`, `Conductor` manteniendo `Role` enum. Estado en mini tarjeta `#F8FAFC`/borde `#E2E8F0`/`CornerRadius=8`/`Padding=12,10` con `MaterialDesignSwitchToggleButton`: Usuario `Usuario Activo` / `Permite iniciar sesión en el POS`; Cliente `Cliente Activo` / `Habilitado para facturación y crédito`. Botón principal `Guardar Usuario`/`Guardar Cliente`/`Guardar Cambios` 44px `#6D28D9`, hover `#5B21B6`, sin sombra pesada.
- S9. Filtro de estado de clientes server-side: extender `GetCustomersAsync` con `bool? isActive` opcional (al final de la firma) en interfaz/fachada/servicio/controller/cliente; `q.Where(c => c.IsActive == isActive)` cuando tenga valor; el ViewModel resetea a página 1 al cambiar. No romper llamadas existentes.
- S10. Tests: filtros de usuarios (texto/rol, conteo), filtro de estado de clientes (cliente y backend), converters de iniciales, XAML estático para textos/estilos/columnas, y suite completa. Visual harness 96/120 DPI sin colisiones.

## Tasks

| ID | Specs | Route | Estado | Commit |
| --- | --- | --- | --- | --- |
| T1 | S9 | inline/backend | completado | — |
| T2 | S2-S8, S9 | inline WPF/VM | completado | — |
| T3 | S10 | inline/tests | completado | — |
| T4 | S10 | harness visual + docs | completado | — |

## Log

- L1 (2026-11-25): Pedido del usuario, verbatim:
  > Actúa como un Senior WPF UI/UX Architect. Vamos a realizar una mejora visual integral nivel Enterprise en la vista de **Gestión de Usuarios y Clientes** (`UsersAndCustomersView.xaml` / vistas y estilos asociados), manteniendo intacta la paleta institucional (`#F8FAFC`, `#FFFFFF`, `#E2E8F0`, acento `#6D28D9`), sin agregar paquetes externos ni romper bindings, comandos o validaciones del ViewModel.
  >
  > ### 1. Cabecera Principal y Pestañas Segmentadas
  > - **Título con identidad:** Coloca el ícono de usuarios dentro de un contenedor cuadrado redondeado (`42x42px`, `CornerRadius="10"`, `Background="#EDE9FE"`, ícono `#6D28D9`), acompañado del título `Gestión de Usuarios y Clientes` (`22px`, `FontWeight="Bold"`, `#0F172A`) y el subtítulo `Administra accesos, roles del personal y el directorio de clientes.` (`13px`, `#64748B`).
  > - **Botón de configuración/menú:** Retira el engranaje gris suelto pegado al título y ubícalo en la esquina superior derecha como botón secundario formal (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `CornerRadius="8"`, `Padding="12,8"`).
  > - **Segmented Control (Pestañas):** Reemplaza los dos botones con sombra oscura por una barra segmentada limpia dentro de un contenedor `Background="#F1F5F9"`, `CornerRadius="10"`, `Padding="4"`:
  >   * Pestaña activa: `Background="#FFFFFF"` (o `#6D28D9` plano sin sombra pesada), `CornerRadius="8"`, texto en `FontWeight="SemiBold"` y un mini contador pill con el total de registros (ej. `Usuarios y Cajeros 8` | `Gestión de Clientes 7`).
  >   * Pestaña inactiva: fondo transparente, texto `#475569` con hover sutil.
  >
  > ### 2. Panel Izquierdo — Tarjeta de Directorio (Paridad total en ambas pestañas)
  > - **Tarjeta contenedora:** Envuelve la tabla, su barra de herramientas y el pie en un `Border` con `Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"` ocupando `Height="*"`.
  > - **Paridad de barra superior en Usuarios y Clientes:**
  >   * En **Gestión de Clientes**, conserva el buscador por Cédula/RIF o Nombre, suaviza su borde a `#CBD5E1` (`CornerRadius="8"`, `Height="40"`), añade un filtro rápido de estado (`Todos`, `Activos`, `Inactivos`) y estiliza `Refrescar` como botón secundario limpio (`#FFFFFF`, borde `#CBD5E1`, texto `#334155`).
  >   * En **Usuarios y Cajeros**, agrega la misma barra superior con buscador por usuario/nombre, filtro rápido por rol (`Todos los roles`, `Admin`, `Gerente`, `Cajero`) y botón `Refrescar`, para lograr simetría total entre pestañas.
  > - **Tabla Enterprise y Cero Truncamientos:**
  >   * Aplica `HeadersVisibility="Column"`, `GridLinesVisibility="Horizontal"`, `HorizontalGridLinesBrush="#F1F5F9"`, `RowHeight="50"`, `BorderThickness="0"` y `ScrollBar` delgado moderno (`Width="6"`).
  >   * **Avatar de iniciales:** Agrega un círculo compacto de `32x32px` con las iniciales del registro (`#EDE9FE` / `#5B21B6` para usuarios; `#F1F5F9` / `#334155` para clientes) antes del nombre para mejorar la legibilidad inmediata.
  >   * **Anchos generosos sin recortes:** Asigna ancho suficiente a `Usuario` (`MinWidth="140"`), `Cédula / RIF` (`MinWidth="130"` con fuente tabular) y `Teléfono` (`MinWidth="140"` con fuente tabular) para que jamás aparezcan recortes como `BOT_STRESS...` o `0414332255...`.
  >   * **Píldoras de Rol y Estado:**
  >     - Estiliza la columna `Rol` con badges diferenciados en español claro: `Admin` (`#EDE9FE` / `#5B21B6`), `Manager`/`Gerente` (`#DBEAFE` / `#1D4ED8`), `Cashier`/`Cajero` (`#FEF3C7` / `#B45309`).
  >     - Mejora el badge `Estado` con un punto indicador (`● Activo` en `#DCFCE7` / `#15803D`; `● Inactivo` en `#F1F5F9` / `#64748B`).
  >   * **Resaltado de fila en edición:** Cuando se seleccione una fila para editarla en el panel derecho, resáltala con fondo `#F5F3FF` y borde indicador izquierdo de `3px` en `#6D28D9`.
  > - **Pie de tabla unificado:** Integra el contador y paginador en el borde inferior de la tarjeta (`BorderThickness="0,1,0,0"`, `BorderBrush="#F1F5F9"`, `Background="#FAFAFA"`, `CornerRadius="0,0,12,12"`) en ambas pestañas.
  >
  > ### 3. Panel Derecho — Tarjeta Inspector / Formulario (`Width="380"` a `400"`)
  > - **Contenedor blanco formal:** Encapsula todo el formulario derecho dentro de su propia tarjeta blanca (`Background="#FFFFFF"`, `BorderBrush="#E2E8F0"`, `BorderThickness="1"`, `CornerRadius="12"`) alineada a la misma altura superior que la tarjeta izquierda.
  > - **Corrección crítica de colisión en cabecera (`Nuevo Usuario` / `Nuevo Cliente` vs. `+ Nuevo`):**
  >   * Crea una barra de cabecera (`Background="#F8FAFC"`, borde inferior `#E2E8F0`, `CornerRadius="12,12,0,0"`, `Padding="20,14"`) usando un `DockPanel` o `Grid` (`*`, `Auto`):
  >     - Izquierda: título dinámico (`Nuevo Usuario` / `Editar Usuario` o `Nuevo Cliente` / `Editar Cliente`, `15px`, `FontWeight="Bold"`, `#0F172A`) + subtítulo breve o badge de modo.
  >     - Derecha: botón compacto `+ Nuevo` (`Padding="10,6"`, `Height="32"`, estilo secundario violeta `#F5F3FF`, borde `#DDD6FE`, texto `#6D28D9`, sin ancho excesivo ni superposición).
  > - **Campos limpios y corrección de bugs del formulario:**
  >   * **Etiquetas arriba de cada campo:** Elimina la etiqueta flotante que corta el borde de `Rol del Usuario *` y usa etiquetas consistentes encima de todos los inputs (`12px`, `FontWeight="SemiBold"`, `#334155`, `Margin="0,0,0,6"`).
  >   * **Inputs estilizados:** `Height="40"`, `CornerRadius="8"`, `Background="#F8FAFC"` en reposo y `#FFFFFF` con foco, `BorderBrush="#CBD5E1"` en reposo y `#6D28D9` (`1.5px`) al enfocar.
  >   * **Corregir placeholder cortado de Contraseña:** Acorta el texto guía a `"Opcional (por defecto: usuario)"` con margen derecho suficiente para no chocar con el ícono del ojo.
  >   * **Eliminar contador duplicado `0 / 50` en Clientes:** Quita el `0 / 50` que aparece duplicado y deja un único contador discreto alineado a la derecha sobre la línea de la etiqueta `Nombre Completo o Razón Social *`, dejando debajo solo el texto de ayuda `"Máximo 50 caracteres para reportes y facturas"` en `11px` `#64748B`.
  >   * **Selector de Rol legible:** Muestra etiquetas claras en español en el `ComboBox` (`Cajero`, `Gerente`, `Administrador`) manteniendo el valor interno del ViewModel sin romper contratos del backend.
  >   * **Fila de estado (`Usuario Activo` / `Cliente Activo`):** Encierra el control en una mini tarjeta horizontal (`Background="#F8FAFC"`, `BorderBrush="#E2E8F0"`, `CornerRadius="8"`, `Padding="12,10"`) usando un `ToggleSwitch` (mismo estilo que Configuración) con título y subtítulo breve (`"Permite iniciar sesión en el POS"` / `"Habilitado para facturación y crédito"`).
  >   * **Botón principal plano y limpio:** `Guardar Usuario` / `Guardar Cliente` (o `Guardar Cambios` al editar) en `Height="44"`, `CornerRadius="8"`, `Background="#6D28D9"` (hover `#5B21B6`), `Foreground="White"`, `FontWeight="SemiBold"`, eliminando la sombra oscura pesada.
  >
  > ### 4. Validación
  > - Compila con `dotnet build -c Release` verificando `0 warnings / 0 errors` y confirma que los tests del módulo de Usuarios y Clientes pasen al 100%, validando visualmente a 100% y 125% de escala DPI que no existan colisiones ni recortes de texto.

- L2 (2026-11-26): **Backend y contrato de filtro**. `GetCustomersAsync` acepta `bool? isActive` al final de la firma en `Sales.Module.Interfaces.ISalesService`, `SalesCustomerService`, la fachada `SalesService.Customers`, el controller `SalesController.Customers` y el cliente WPF `ISalesService`/`SalesService`. El servicio aplica `c.IsActive == isActive` antes del conteo/paginación; el cliente envía `&isActive=true|false` solo cuando tiene valor. Prueba de servicio `CoverageConvergenceTests.GetCustomersAsync_FiltersByActiveStatus` y contrato HTTP `ClientHttpContractTests.Sales_GetCustomersAsync_ForwardsTheActiveStatusFilter`.
- L3 (2026-11-26): **WPF Enterprise UI**. `UsersManagementView.xaml` reescrita con cabecera 42x42, segmented control con contadores, tarjetas de directorio e inspector de 390px, toolbars simétricas (búsqueda + filtro + Refrescar), tabla con avatares 32x32, badges de rol/estado, fila seleccionada `#F5F3FF` con borde izquierdo 3px, footer unificado y formularios con etiquetas arriba, inputs 40px, ComboBox legible, contador único `/50`, ToggleSwitch de estado y botón primario plano. `UsersManagementViewModel` agrega `TotalUserCount`, `UserSearchQuery`, `SelectedUserRoleFilter`, `RoleOptions` y `PasswordPlaceholder`; `CustomerManagementViewModel` agrega `SelectedCustomerStatusFilter` y corrige el formateo de RIF con dígito verificador. Converter `InitialsConverter` para avatares.
- L4 (2026-11-26): **Validación visual**. Harness WPF temporal fuera del repo (`PickerRender`) renderizó las pestañas Usuarios y Clientes con datos de prueba a 96 y 120 DPI. Se verificó: sin solapamiento `Nuevo Usuario`/`Nuevo Cliente` vs `+ Nuevo`; avatares, badges, columnas de cédula/teléfono y estados visibles; nombre largo de cliente envuelto en dos líneas sin recorte; `BOT_STRESS_VENTAS_NOCTURNA` completo; formularios y toggles alineados. Los PNG quedaron en `C:\Users\Lenovo IdeaPad 3\AppData\Local\Temp\opencode\PickerRender\bin\Release\net10.0-windows\shots\`.
- L5 (2026-11-26): **Verificación final**. `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errors. Suite .NET completa con PostgreSQL real: **4522/4522**, 0 skipped. Focused Usuarios/Clientes/contratos: **158/158**. No se tocaron bindings de comandos/validaciones existentes ni se agregaron paquetes.
