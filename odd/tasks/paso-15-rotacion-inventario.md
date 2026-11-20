# Paso 15 - Rotacion de inventario y stock muerto

Rama: `V0.15`. Entrega: work units locales; push/PR a decision del mantenedor.
Runner: `dotnet build CommandCenter.slnx -c Release` + `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; Web: `npm test`, `npm run lint`, `npm run build`.
Estrategia de entrega: `ask-on-risk`.
TDD: RED observado antes de cada comportamiento; GREEN; refactor con tests verdes.

## Specs

### S1 - Metricas de rotacion
La ventana de ventas por defecto es 90 dias UTC (`SalesWindowDays`, rango 1..365). Se cuentan solo lineas de ventas de mercancia emitidas y no anuladas: `Sale.SaleType == Merchandise`, `Sale.InvoiceNumber != null`, `Sale.Status == Completed`, `Sale.CancelledAtUtc == null` y `Sale.Date` dentro de `[fromUtc, toUtc)`.
Por producto:
- `UnitsSold` = suma de `SaleItem.Quantity` en la ventana.
- `SalesRevenueUsd` = suma de `SaleItem.Subtotal` (snapshot USD) en la ventana.
- `SalesVelocityPerDay` = `UnitsSold / SalesWindowDays`.
- `AverageInventoryDays` = `StockQuantity / SalesVelocityPerDay` cuando `SalesVelocityPerDay > 0`; `null` cuando no hay ventas en la ventana.
Por categoria se agregan unidades vendidas, stock y valor de inventario; los dias promedio de la categoria usan `StockTotal / (UnitsSoldTotal / SalesWindowDays)`.

### S2 - Clasificacion de salud
Bandas por producto, en este orden:
- `DeadStock`: `StockQuantity > 0` y `DaysSinceLastSale >= DeadStockDays`.
- `High`: no muerto y `AverageInventoryDays <= 30`.
- `Medium`: no muerto y `30 < AverageInventoryDays <= 60`.
- `Low`: no muerto y (`AverageInventoryDays > 60` o sin ventas en la ventana).
`DeadStockDays` por defecto 60, rango 1..365; la UI ofrece 45, 60 y 90.
`DaysSinceLastSale` usa la ultima venta valida historica del producto; si nunca vendio, usa `Product.CreatedAt`. `LastSaleDateUtc` queda `null` cuando nunca vendio.

### S3 - Capital inmovilizado
`InventoryValueUsd` = `StockQuantity * CostPriceUSD` por producto.
`ImmobilizedCapitalUsd` = suma de `InventoryValueUsd` de los productos `DeadStock`.
`ImmobilizedCapitalPercent` = `ImmobilizedCapitalUsd / TotalInventoryValueUsd * 100`; 0 si el denominador es 0.
El universo del reporte son productos no borrados, no `IsCashAdvance`, no `IsGroupHeader`; se incluyen inactivos para no ocultar capital retenido y se expone `IsActive`.

### S4 - Filtros y paginacion
Filtros: `SalesWindowDays`, `DeadStockDays`, `CategoryId` (subarbol incluido por defecto), `SearchText` (nombre o SKU), `RotationClass` opcional, `SortBy` (`immobilizedCapital`, `daysSinceLastSale`, `averageInventoryDays`, `unitsSold`, `name`, `stock`) y `Descending`.
Paginacion server-side: `Page` >= 1, `PageSize` 1..200 (default 50). El resumen y las categorias cubren todo el universo filtrado por categoria/busqueda; `Products` aplica ademas `RotationClass` y la pagina pedida, para que el grafico ejecutivo conserve la distribucion completa.
Categoria por producto: primera categoria por `CategoryId` ascendente, igual que `GetProductCategoryInfoAsync`; productos sin categoria se agrupan como `Sin categoria` solo en el desglose, sin inventar una entidad.

### S5 - Puente a liquidacion masiva
Endpoint de IDs de stock muerto devuelve hasta 1000 IDs (`BulkUpdatePricesAsync` limita a 1000), `TotalCount` real y `Truncated`.
El boton `Aplicar Promocion / Descuento` abre el dialogo de Edicion Multiple de Precios (Paso 9) con seleccion explicita de esos IDs, sin filtros implicados. Si `TotalCount > 1000`, la UI avisa y no abre el dialogo hasta que el usuario refine el filtro.
El dialogo conserva todas las reglas de precio, redondeo, pisos y RBAC del Paso 9; este paso no duplica el motor.

### S6 - Informe ejecutivo
El reporte expone `HighRotationCount`, `MediumRotationCount`, `LowRotationCount`, `DeadStockCount`, sus porcentajes sobre productos, `TotalInventoryValueUsd` e `ImmobilizedCapitalUsd`.
La UI muestra tarjetas KPI y barras comparativas nativas (sin librerias externas) para Alta Rotacion, Media Rotacion y Capital Inmovilizado, una tabla de productos y una tabla/desglose por categoria.
La vista WPF se abre desde Inventario; la vista Web se agrega como pagina `inventory-health` visible para Admin/Manager.

### S7 - Rendimiento PostgreSQL
Las agregaciones se ejecutan en SQL con `AsNoTracking`, `GroupBy` y proyecciones, sin cargar `SaleItems` a memoria.
Se agregan indices idempotentes: `IX_Sales_Date_SaleType_InvoiceNumber` y `IX_SaleItems_ProductId_SaleId`.
La consulta de ultima venta usa `SaleItems` + `Sales` con los indices existentes; no se crea una vista materializada en esta fase porque los datos deben reflejar ventas recientes sin refresco manual.

### S8 - RBAC y errores
`GET /api/inventory-rotation/report` y `GET /api/inventory-rotation/dead-stock-ids` requieren `RequireAdminOrManager` y `RequireSecurityStampValidation`.
Entradas invalidas responden 400 via `ApiBadRequest`; no se expone ningun dato de costo a roles no autorizados.

## Tasks

- T1 | S1-S4/S7 | Backend DTOs, calculadora pura, snapshot de inventario, servicio de reporte, indices y migracion | delegated | completed (build 0/0; calculator 5/5, service 4/4, catalog 2/2, controller 3/3)
- T2 | S5/S8 | Endpoints de reporte e IDs de stock muerto, RBAC y pruebas de contrato | delegated | completed (3/3 controller tests; RBAC attributes)
- T3 | S6 | Vista WPF de salud de inventario, tarjetas/barras/tablas y puente al Paso 9 | delegated | completed (build 0/0; VM 4/4)
- T4 | S6 | Pagina Web `inventory-health`, navegacion y puente al Paso 9 | delegated | completed (Web 810/810; lint limpio; build exitoso)
- T5 | S1-S8 | Verificacion integral (build 0/0, tests .NET/Web, lint) y ANEXO en `docs/reporte.txt` | delegated | in progress (full .NET 4395 pass, 60 Postgres-lane env failures; Web green)

## Log

### L1 - Requerimiento original
15. Analisis de rotacion de inventario y deteccion de stock inmovilizado ("Stock Muerto")
Objetivo: Identificar productos con baja o nula rotacion para sugerir liquidaciones masivas y optimizar el capital de trabajo inmovilizado en el inventario.

Descripcion detallada:

Metricas de rotacion comercial:
Calculo de dias promedio de inventario y velocidad de venta por producto y por categoria, utilizando los datos de ventas consolidados en el Paso 14.

Alertas de stock muerto / estancado:
Deteccion automatica de articulos que superen un umbral sin ventas (ej. mas de 45, 60 o 90 dias), calculando el valor monetario total del capital retenido en bodega.

Puente de accion directa a liquidacion masiva:
Desde el mismo reporte, un boton "Aplicar Promocion / Descuento" enviara automaticamente la lista de productos estancados al modulo de Edicion Multiple de Precios (Paso 9) para aplicar una rebaja porcentual rapida o remate por categoria.

Informe ejecutivo de salud de inventario:
Graficos que muestren el porcentaje del inventario en: Alta Rotacion, Media Rotacion y Capital Inmovilizado.

Aspectos tecnicos:
Consultas optimizadas con vistas indexadas o tablas agregadas en PostgreSQL.
Mapeo directo hacia `BulkUpdatePricesAsync` con seleccion prefiltrada.

Beneficios:
Recupera liquidez financiera retenida en productos obsoletos.
Evita perdidas por vencimiento o merma de articulos sin salida.
Cierra el ciclo completo entre inventario, precios, ventas y analitica.

Justificacion:
- Dificultad: Media - Alta.
- Efecto Dominio: Es el broche de oro del sistema gerencial. No podria existir sin la auditoria de stock (Paso 5 y 6), las categorias (Paso 8), la edicion masiva de precios (Paso 9) y el motor analitico de ventas (Paso 14). Conecta la inteligencia del negocio directamente con la accion operativa de catalogo.

### L2 - Decisiones tecnicas
- 2026-11-16: La ventana de ventas y el umbral de stock muerto son parametros separados; el umbral de 45/60/90 decide `DeadStock`, la ventana de 90 alimenta velocidad y dias promedio.
- 2026-11-16: `DeadStock` tiene prioridad sobre las bandas de rotacion.
- 2026-11-16: El reporte se calcula on-demand (sin tabla agregada) para reflejar ventas recientes; se mitiga con indices y agregaciones SQL.
- 2026-11-16: El puente usa seleccion explicita de IDs para no heredar filtros ambiguos del catalogo.

### L3 - Evidencia de implementacion
- RED observado: `InventoryRotationCalculatorTests` fallo 5/5 con `NotImplementedException` antes de implementar la calculadora.
- GREEN backend: calculadora 5/5; servicio 4/4; snapshot de catalogo 2/2; controlador 3/3; build Release 0/0.
- Migracion `20261117022102_AddInventoryRotationIndexes`: DDL idempotente `CREATE INDEX IF NOT EXISTS` para `IX_Sales_Date_SaleType_InvoiceNumber` e `IX_SaleItems_ProductId_SaleId`.
- WPF: `InventoryHealthDialog`, `InventoryHealthViewModel`, boton en Inventario y puente al `BulkPriceEditDialog`; build 0/0 y VM 4/4.
- Web: pagina `inventory-health`, sidebar/RBAC, servicio API y modal de descuento; `npm test` 810/810, `npm run lint` limpio, `npm run build` exitoso.
- Suite .NET completa: 4395 superados; 60 fallos exclusivamente del carril PostgreSQL real por falta de `TEST_POSTGRES_CONNECTION`. No hubo fallos funcionales fuera de ese carril.
- No se hicieron commits ni push en esta sesion.
