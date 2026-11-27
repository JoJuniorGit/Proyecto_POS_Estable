# Conteo Físico — Alcance por Productos (3 WUs)

Objetivo: permitir sesiones de conteo con alcance estricto a productos seleccionados, sin tocar migraciones previas, con persistencia relacional, contrato API, servicio, WPF y Web.
Branch: `V0.15` (rama actual, sin push).
Delivery: `auto-chain` no aplica; 3 work units con commit local por WU y ANEXO LXXIII en `docs/reporte.txt`.
Test runner: `dotnet build CommandCenter.slnx -c Release` + `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; Web: `npm test`, `npm run lint`, `npm run build` en `Web.Frontend`.

## Specs

- S1. Persistencia: agregar `StockCountScopeType` (`All = 0`, `Category = 1`, `Products = 2`) y `StockCountSession.ScopeType`; tabla puente `StockCountSessionProducts` con clave única compuesta `(StockCountSessionId, ProductId)` y FKs `DeleteBehavior.Restrict`; migración nueva idempotente con backfill (`ScopeCategoryId IS NOT NULL -> Category`, caso contrario `All`), `ModelSnapshot` actualizado y verificación de `MigrationIndexDriftTests` y `PendingModelChangesTests`. No modificar migraciones previas.
- S2. Contrato: `CreateStockCountRequestDto` acepta `ScopeType` y `ScopeProductIds` (nullable/lista). Validación de exclusividad: `All` sin `CategoryId` ni productos; `Category` con `CategoryId` obligatorio y sin productos; `Products` con 1 a 1000 IDs únicos, sin `CategoryId`, todos existentes, contables y sin ítems no inventariables como `IsCashAdvance == true`.
- S3. Servicio: `EnsureProductInScopeAsync` rechaza con `409 Conflict` y mensaje claro de operador cuando el producto no está en `StockCountSessionProducts`; `CountUnscannedInScopeAsync` cuenta solo los seleccionados no escaneados; `ApplyAsync` sigue aplicando únicamente lo escaneado; el DTO de sesión expone la etiqueta `"Productos seleccionados (N)"`.
- S4. Pruebas: unitarias e integración PostgreSQL para alta con productos, duplicados/tope/no-contables (`400`), escaneo fuera de alcance (`409`), review de no escaneados, apply solo de escaneados y regresión completa de `All` y `Category`.
- S5. WPF/Web: selector de alcance `Todo el inventario` / `Por categoría` / `Productos seleccionados`; diálogo selector de productos reutilizable con búsqueda por nombre/SKU/código de barras, filtro opcional por categoría, selección múltiple, contador `N / 1000` y chips/lista para revisar o quitar.
- S6. Escaneo: aviso visual inmediato cuando el backend devuelva `409` por producto fuera de alcance, en WPF y Web.
- S7. Validación final: `dotnet build -c Release` (0 warnings / 0 errors), suite `.NET` al 100%, y `npm test` + `npm run lint` + `npm run build` en Web.

## Tasks

| ID | Specs | Route | Estado |
| --- | --- | --- | --- |
| T1 / WU1 | S1 | inline (parent, contrato EF/migración crítico) | pendiente |
| T2 / WU2 | S2, S3, S4 | inline (parent, contrato API/servicio crítico) | pendiente |
| T3 / WU3 | S5, S6, S7 | delegated (worker WPF y worker Web, secuencial) | pendiente |

## Log

- L1 (2026-11-25): Pedido del usuario, verbatim:
  > Decisión cerrada: vamos con **Alcance Estricto** (`ScopeType = All | Category | Products`) y tabla puente `StockCountSessionProducts`. Cualquier escaneo de un producto que no pertenezca a los seleccionados en una sesión `Products` debe rechazarse con `409 Conflict` y un mensaje claro para el operador.
  >
  > Ejecuta el cambio en **3 Work Units** bajo TDD (`RED -> GREEN`) en la rama actual sin modificar migraciones previas:
  >
  > ### WU1 — Persistencia y Migración Idempotente
  > - Agrega el enum `StockCountScopeType` (`All = 0`, `Category = 1`, `Products = 2`) y el campo en `StockCountSession`.
  > - Crea la tabla puente `StockCountSessionProducts` (`StockCountSessionId`, `ProductId`) con clave única compuesta `(StockCountSessionId, ProductId)` y FKs con `DeleteBehavior.Restrict`.
  > - Genera una migración nueva idempotente que haga backfill de sesiones existentes (`CategoryId IS NOT NULL -> Category`, caso contrario `All`), actualiza `ModelSnapshot` y verifica `MigrationIndexDriftTests` y `PendingModelChangesTests` sobre PostgreSQL.
  >
  > ### WU2 — Contrato API, Servicio y Pruebas de Integración
  > - Extiende `CreateStockCountRequestDto` con `ScopeType` y `ScopeProductIds` (nullable/lista).
  > - Valida exclusividad según `ScopeType`:
  >   * `All`: sin `CategoryId` ni `ScopeProductIds`.
  >   * `Category`: `CategoryId` obligatorio, sin `ScopeProductIds`.
  >   * `Products`: `ScopeProductIds` obligatorio (entre 1 y 1000 IDs únicos), sin `CategoryId`; valida que todos existan, sean contables y excluye ítems no inventariables (como `IsCashAdvance == true`).
  > - Actualiza `EnsureProductInScopeAsync` (estricto con `409` si el producto no está en `StockCountSessionProducts`), `CountUnscannedInScopeAsync` (solo seleccionados no escaneados), mantiene `ApplyAsync` aplicando únicamente lo escaneado y expone en el DTO de sesión la etiqueta `"Productos seleccionados (N)"`.
  > - Cubre con tests unitarios e integración en PostgreSQL: alta con productos, duplicados/tope/no-contables (400), escaneo fuera de alcance (409), review de no escaneados, apply solo sobre escaneados y regresión completa de `All` y `Category`.
  >
  > ### WU3 — Selector de Productos Reutilizable en WPF y Web
  > - En el formulario de nueva sesión (WPF y Web), habilita el selector de alcance `Todo el inventario` / `Por categoría` / `Productos seleccionados`.
  > - Construye un diálogo selector de productos reutilizable (en WPF y Web) con búsqueda por nombre/SKU/código de barras, filtro opcional por categoría, selección múltiple con contador (`N / 1000`) y chips/lista para revisar o quitar ítems elegidos.
  > - Muestra en el flujo de escaneo un aviso visual inmediato cuando el backend devuelva `409` por producto fuera de alcance.
  > - Verifica `dotnet build -c Release` (0 warnings / 0 errors), suite `.NET` al 100%, y `npm test` + `npm run lint` + `npm run build` en Web.

- L2 (2026-11-25): Decisiones de implementación:
  - `ScopeType` se persiste como string(32) por consistencia con `StockCountStatus`; el JSON de la API usa el enum numérico (sin `JsonStringEnumConverter` en el repo).
  - `CreateStockCountRequestDto.ScopeType` queda nullable para retrocompatibilidad: si falta, se infiere `Category` cuando viene `ScopeCategoryId`, `Products` cuando vienen IDs y `All` en el resto. Con `ScopeType` explícito se valida exclusividad estricta.
  - El DTO de sesión conserva la firma posicional existente; `ScopeType`, `ScopeProductCount` y `ScopeDisplay` se agregan como propiedades `init`/calculadas para no romper constructores actuales.
  - La etiqueta de alcance se calcula server-side: `Todo el inventario`, la ruta de categoría o `Productos seleccionados (N)`.
  - El selector WPF usará `IProductService.GetPagedAsync(..., categoryId)` (ya soporta búsqueda y categoría) y un ViewModel de diálogo testeable; el Web usará el endpoint paginado `/api/products` con `statusFilter=active` y `categoryId`.
  - El aviso de fuera de alcance se muestra con el mensaje del backend; en Web se distingue `409` + mensaje y en WPF se detecta el mensaje de productos seleccionados.
  - No se agregan librerías externas.

- L3 (2026-11-25): **WU1 COMPLETADA (TDD RED -> GREEN)**. Se agregó `StockCountScopeType` (`All/Category/Products`), `StockCountSession.ScopeType` + navegación `ScopeProducts`, la entidad `StockCountSessionProduct` y el mapeo EF con PK compuesta `(StockCountSessionId, ProductId)`, índice `IX_StockCountSessionProducts_ProductId` y FKs `Restrict`. La migración `20261125173504_AddStockCountProductScope` es nueva, aditiva e idempotente (`ADD COLUMN IF NOT EXISTS`, backfill `ScopeCategoryId IS NOT NULL -> Category`, `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`) y no modifica migraciones previas. RED: `CS0117/CS0246` al compilar `StockCountProductScopeModelTests`; GREEN: modelo 2/2, `MigrationIndexDriftTests` 2/2, `PendingModelChangesTests` 2/2 y smoke PostgreSQL real 1/1 (migración a la versión previa, filas legacy, backfill, PK/FKs Restrict e índice). Pendiente WU2/WU3.

- L4 (2026-11-25): **WU2 COMPLETADA (TDD RED -> GREEN)**. `CreateStockCountRequestDto` acepta `ScopeType` nullable y `ScopeProductIds`; el servicio infiere el alcance en requests legacy y valida exclusividad estricta. `Products` exige 1..1000 IDs únicos, existentes, activos, no borrados y contables (rechaza cash advance, agrupador individual y variante de stock compartido) con `400`; `Category` conserva el `404` de categoría inexistente. El alta persiste la tabla puente en el mismo `SaveChanges`. `EnsureProductInScopeAsync` usa el tipo de alcance y devuelve `409` con nombre/SKU en el mensaje para Products; `CountUnscannedInScopeAsync` cuenta solo los seleccionados no escaneados; `ApplyAsync` no cambió y sigue aplicando solo los ítems escaneados. `StockCountSessionDto` expone `ScopeType`, `ScopeProductCount` y `ScopeDisplay` calculado (`Todo el inventario`, ruta de categoría, `Productos seleccionados (N)`) sin romper la firma posicional existente. RED: CS1739/CS1061 en `StockCountProductScopeServiceTests`; GREEN: 7/7 unitarios (incluye round-trip JSON web), 66/66 del filtro `StockCount` con PostgreSQL real, incluido el flujo create Products -> scan dentro/fuera -> review -> close -> apply. Pendiente WU3.

- L5 (2026-11-25): **WU3 COMPLETADA**. WPF: `ProductPickerDialogViewModel` + `ProductPickerDialog` reutilizables (búsqueda paginada por nombre/SKU, filtro por categoría, solo productos contables, chips, contador N/1000 y tope), integrados al alta con `SelectedCreateScopeType`, `CreateScopeProducts` y aviso ámbar `ScanFeedbackIsScopeViolation` para el 409 de fuera de alcance. Web: `ProductPickerModal` reutilizable con búsqueda debounced, categoría, paginación, chips y contador; el alta envía `scopeType`/`scopeProductIds`, la lista/detalle usan `scopeDisplay` y el scan muestra un aviso `scope` (`role=alert`) para el 409. El selector no muestra stock, costo ni precio (conteo ciego). Evidencia: `dotnet build CommandCenter.slnx -c Release` 0/0; suite .NET completa con `TEST_POSTGRES_CONNECTION` **4486/4486**; Web `npm test` **818/818**, `npm run lint` limpio y `npm run build` exitoso; wwwroot regenerado. WPF no se inspeccionó visualmente; el E2E full-stack sigue gateado por entorno.
