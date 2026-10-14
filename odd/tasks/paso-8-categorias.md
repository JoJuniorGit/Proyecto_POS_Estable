# Paso 8 del Roadmap — Categorías jerárquicas del catálogo

Objetivo: organizar los productos en categorías jerárquicas (padre/hijas, múltiples por producto) con gestión CRUD validada, asignación desde los formularios, filtros en catálogo (WPF + Web) y búsqueda POS, e importación/exportación con columna de categoría — habilitador estructural de la edición masiva de precios (Paso 9) y de los reportes por categoría (Pasos 14-15). Tramo 8.179. Branch: V0.15. Entrega: work units locales (push/PR = decisión del mantenedor). RDD: off (clone-local). Tests: build `dotnet build CommandCenter.slnx -c Release`; carriles `--filter "FullyQualifiedName~Categor|FullyQualifiedName~Product|FullyQualifiedName~Catalog|FullyQualifiedName~Inventory"`.

## Specs

Fuente: `docs/Ideas.txt` Paso 8 (sincronizado 2026-10-13) + decisión del mantenedor de arrancar por este paso. Supuestos marcados.

- **S1 (Modelo de datos)**: `Category` jerárquica (self-FK `ParentCategoryId`, profundidad libre) + junction `ProductCategory` (N:M — un producto puede tener varias categorías). Nombres: único por padre (case-insensitive). Migración idempotente (guards `to_regclass`/`information_schema`, estilo 8.177) + `.Designer.cs` + snapshot consistente (test de drift contra Postgres). Índices: `ParentCategoryId`, `NormalizedName`, y único `(ProductId, CategoryId)` en el junction. FKs: hijas bloquen borrado del padre; junction Cascade desde Product.
- **S2 (Gestión CRUD validada)**: API `/api/categories` (GET árbol/listado para Admin/Manager/Cashier; mutaciones Admin/Manager + security stamp): crear, renombrar, mover de padre, borrar. Validaciones: nombre no vacío ≤60 con trim; sin duplicados por padre (normalizado); **sin ciclos** al mover (una categoría no puede ser ancestro de sí misma); borrado bloqueado (409 legible) si tiene hijos o productos asignados.
- **S3 (Asignación desde formularios)**: `CreateProductDto`/`UpdateProductDto` aceptan `CategoryIds` (validadas existentes, set completo reemplaza); `ProductDto` expone `Categories` (id + nombre + ruta legible). WPF `ProductDialog`: card "Categorías" con multi-selección (lista indentada del árbol) + botón "Gestionar categorías" (nuevo diálogo, patrón `ManageVariantsAsync`). Import: columna "Categoría" (ver S5).
- **S4 (Filtros)**: `GetProductsPagedAsync` y `GET /api/products` aceptan `categoryId` opcional que filtra por **subárbol completo** (la categoría y todas sus descendientes — set calculado en servicio, provider-agnóstico); `GET /api/products/suggestions` acepta `categoryId` para la búsqueda por categoría en POS Web. WPF `InventoryView`: filtro categoría junto a Estado. Web `CatalogPage`: sidebar/picker de categorías conectado al fetch paginado.
- **S5 (Importación/Exportación)**: import acepta columna "Categoría"/"Categoria"/"Category" (mapeo por Contains existente) con **ruta jerárquica separada por `>`** (ej. `Alimentos > Lácteos`); segmentos inexistentes se **auto-crean** (raíz→hoja, case-insensitive, documentado); export CSV/XLSX agrega columna "Categorías" con las rutas asignadas separadas por `; `. (supuesto: una categoría por celda en import v1; múltiples en UI.)
- **S6 (Proceso)**: build 0/0, carriles + suite completa por partes, verificación independiente, ANEXO 8.179, commits en V0.15. Los reportes "ganancias/rotación por categoría" NO son parte de este tramo (los consumen P14/P15).

## Hallazgos de exploración (L2)

- Greenfield total (0 ocurrencias de "Categor" de dominio). Molde N:M = `SupplierProductCode` (`Core/Entities/SupplierProductCode.cs` + config `InventoryDbContext.cs:235-249`).
- Migraciones recientes: SQL hand-written idempotente + Designer + Snapshot (drift test contra PG real en `WebApplicationFactorySmokeTests`). Aplicación vía `DatabaseInitializer.MigrateAsync()`.
- La firma de filtros es plana y replica en 4 capas: `IInventoryService.GetProductsPagedAsync` → `ProductsController` (`[FromQuery]`) → `ProductService` WPF (URL builder) → `CatalogPage.jsx` (fetch directo). Se agrega `categoryId` como parámetro opcional AL FINAL para no romper posicionales.
- WPF: ProductDialog sin tabs (cards en grid 2 columnas; slot natural en columna izquierda); no hay control multi-select reutilizable — el precedente de gestión es el diálogo `VariantManagement` con botón en el formulario.
- Web: CatalogPage sin sidebar y sin CRUD de producto; POS usa `SearchBar → getProductSuggestions`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1, S2, S3 (API), S4 (API), S5 | delegated (writer) | Backend: entidades + migración + `CategoryService`/controller CRUD con validaciones + asignación en productos + filtro por subárbol + suggestions + import/export + tests | hecho (writer) — `Category`/`ProductCategory` + migración idempotente `20261014000349` (snapshot limpio), `CategoryService` + `CategoriesController` (unicidad por padre incl. raíces, sin ciclos al mover, borrado bloqueado 409), asignación N:M (semántica null/[]/lista), filtro por subárbol en catálogo/suggestions, import con ruta `>` auto-creante + columna "Categorías" en export/template, 40 tests. Evidencia: build 0/0; carril 434/434 (writer + padre); `has-pending-model-changes` limpio. Verificación independiente: REVISAR sin bloqueantes (residuales: índice parcial para raíces en PG, longitud de segmento en import, edge MaxLength DTO vs trim, staging especial en ProductCrud). Commit de la serie (hash en T4) |
| T2 | S3 (WPF), S4 (WPF) | delegated (writer) | Desktop: card Categorías en `ProductDialog` (multi-select + diálogo de gestión) + filtro en `InventoryView` + cliente `ICategoryService` + mapeo de import + tests | pendiente |
| T3 | S4 (Web), S5 (UI import web n/a) | delegated (writer) | Web: filtro de categorías en `CatalogPage` (sidebar/picker + fetch) + categoría en búsqueda POS + tests livianos | pendiente |
| T4 | S6 | inline (+ verify) | Build + carriles + suite + verificación independiente + ANEXO 8.179 + commits | pendiente |

Estrategia de entrega: `ask-on-risk` → work units locales en V0.15 (T1→T2→T3 secuenciales; el contrato de API se congela en T1). Forecast ~1400–2000 líneas autoradas.

## Log

- L1 (2026-10-13) — Pedido del mantenedor: "Arranca con el paso 8 categorias" (tras la sincronización del roadmap `18c860c`, con reordenamiento propuesto que pone Categorías primero).
- L2 (2026-10-13) — Exploración read-only (agente explore) + decisiones: junction N:M; filtro por subárbol con set de IDs calculado en servicio (sin CTE recursivo, para mantener tests InMemory/SQLite); import con ruta `>` y auto-creación; `categoryId` opcional al final de las firmas; RBAC lectura Admin/Manager/Cashier y mutación Admin/Manager; gestión WPF como diálogo nuevo (patrón VariantManagement).

- L3 (2026-10-13) — T1 backend ejecutado y verificado:
  - Contrato HTTP/DTOs congelado: `GET /api/categories` (`{id,name,parentCategoryId,depth,path}` con `path` "A > B > C"); POST/PUT/DELETE Admin/Manager (+409 duplicado/ciclo/borrado); productos con `categoryIds` (Create: null/[] = sin categorías; Update: null = no tocar, []=quitar todas, lista=reemplazo) y `categories` en el DTO; `categoryId` en list/suggestions (subárbol); import `categoryPath` (`>`, auto-creación) y export/template con columna de categorías.
  - Evidencia: build slnx 0/0; carril `~Categor|~Product|~Inventory|~Catalog` 434/434 (writer y padre); `dotnet ef migrations has-pending-model-changes` limpio (writer y verificador).
  - Verificación independiente: REVISAR sin bloqueantes — residuales anotados para el ANEXO: índice único parcial para raíces (el índice PG no cubre NULL), segmentos >60 chars en import devuelven 500 (sugerido 400/truncado), edge `MaxLength(60)` del DTO vs trim del servicio, y nota de staging (WIP `.AsSplitQuery()` fusionado en el mismo hunk que T1 en `ProductCrud.cs` → commit por blob). Riesgos CI: SQL de migración y smoke PG real quedan para CI.
  - Pendiente: commit T1 (split especial) y arranque de T2 (WPF).
