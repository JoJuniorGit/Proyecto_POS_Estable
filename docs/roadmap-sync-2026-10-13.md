# Reporte Estratégico — Sincronización del Roadmap (Ideas.txt) con el Historial de Commits

**Fecha:** 2026-10-13 · **Rama:** V0.15 · **Commit de referencia:** `f2aa030` · **Alcance:** últimos ~50 commits + verificación de código sobre el árbol actual.

Este informe acompaña la actualización de `docs/Ideas.txt`: los pasos completados quedaron marcados `[x]` con evidencia de código/commits, y los pendientes llevan notas donde el código ya fusionado cambió el terreno.

---

## 1. Estado de conciliación (15 pasos)

| Paso | Estado | Evidencia clave |
|------|--------|-----------------|
| 1. Comportamiento de cierre | ✅ COMPLETADO | 8.120 → `7f0c18c` (cierre seguro + recuperación Web/WPF: `useShutdownGuard`, `SaleRecoveryModal`, `SaleRecoveryStore`); 8.84 → `f127609` (`IAppShutdown`, cierre no bloqueante); liberación de SignalR/servicios en `StopServicesAsync`. |
| 2. Parked Orders multiterminal | ✅ COMPLETADO | 8.129 → `4d2a187` (claims obligatorios), migración `AddHoldOrderClaimsToSales` (2026-09-14), `PendingOrdersView`/`PendingOrdersPage`, F4 en `usePosHotkeys`, expiración + recuperación con tasa vigente (8.143 → `3949dcf`). |
| 3. Controles de sesión | ✅ COMPLETADO con matiz | `InvalidateUserSessionsAsync` (security stamp) en alta/edición/password/estado (`UsersController`); revocación con push SignalR `ForceDisconnect` (8.157 → `53731df`); JWT 720 min (8.143 → `1102254`). **Matiz:** sin vista de sesiones por dispositivo (IP/fecha/kick individual); la revocación es por usuario. |
| 4. Autorizaciones remotas SignalR | ✅ COMPLETADO | Serie 8.150 (`541124d`, `75ce3a2`, `4e9e5f1`, `a7e6770`, `c372d3b`), cierre de residuales 8.151, revocación 8.157. Clientes WPF/Web con espera bloqueante, autorización local y retry por token. |
| 5. Auditoría de ajustes de stock | ✅ COMPLETADO (8.177) | `04d5b19` + `fded8aa` + `48768f6`; ANEXO 8.177. **Desvío aceptado:** sin tabla `StockAdjustmentsAudit`; auditoría por enriquecimiento de `StockMovement` + espejo íntegro en `StockMovements_Archive`, consulta unión activa+archivo con filtros/KPIs/paginación, export CSV/XLSX/PDF legal. |
| 6. Conteo físico ciego móvil | ⏳ PENDIENTE | Escáner Web ya operativo (8.109); sin entidades `StockCountSession`/`StockCountItem`. |
| 7. Grupos de variantes visuales | ✅ COMPLETADO | `AddProductVariantsFinal` (2026-08-31) + `ProductVariantsTests`; agrupación/badges en catálogo Web; `VariantManagementDialog` (WPF) y selectores POS (`VariantSelectorModal` / `VariantSelectionDialog`). |
| 8. Categorías | ⏳ PENDIENTE | Verificado: 0 entidades `Category`/`ProductCategory` en el modelo. |
| 9-10. Precios masivos + redondeo | ⏳ PENDIENTE | Sin `BulkUpdatePricesAsync`; ver corrección §3.4 (régimen multi-moneda). |
| 11-12. Crédito + bimonetario | ⏳ PENDIENTE | Sin entidades `CreditSales`/`CreditPayments`; base multi-moneda y autorizaciones ya disponibles (§3.5). |
| 13. Caja chica | ⏳ PENDIENTE | Sin entidad `PettyCashExpense`; integración con arqueo actual (§3.6). |
| 14-15. Ganancias + rotación | ⏳ PENDIENTE | Pueden capitalizar el Kardex (§3.7); dependencias por P9/P11-13. |

---

## 2. Funcionalidades fusionadas FUERA de la secuencia (capital ya construido)

1. **Facturas de proveedor + OCR (8.144–8.178):** ingesta tabular y OCR con plantillas por proveedor, vinculación con aprendizaje de alias (1:1 y 1:N), detección de tasa impresa, reactividad global moneda/tasa y el Kardex (Paso 5). Todo este módulo llegó antes que muchos pasos del plan.
2. **Multi-moneda Bs.S con tasa BCV (8.9x–8.104):** snapshots inmutables de tasa (`AppliedRate` histórico), precio Bs.S canónico por techo, cierres/arqueos, adelantos de efectivo, retiros parciales/custodia (8.145).
3. **Autorizaciones remotas + revocación de sesiones + suites E2E** (PostgreSQL real + SignalR): infraestructura de confianza que los pasos financieros pueden consumir de inmediato.

**Implicación:** varios pasos futuros llegan a terreno más maduro que el previsto — el crédito bimonetario (P12), la caja chica (P13) y las ganancias (P14) requieren menos andamiaje nuevo del que su justificación original suponía.

---

## 3. Correcciones arquitectónicas (dependencias y desalineaciones detectadas)

1. **Paso 6 asume `StockAdjustmentsAudit`** (del Paso 5 original) que no existe: el modelo real es `StockMovement` enriquecido (`OriginType`, `ReasonCode`, `UnitCostUSD`, `Observation`) + espejo en `StockMovements_Archive`. La sesión de conteo debe escribir ajustes vía `UpdateStockBatchAsync` con `ReasonCode=PhysicalCountCorrection` y observación obligatoria; si no, los conteos caerían "sin clasificar" en el Kardex.
2. **Rol "Auditor"**: el Paso 5 original pedía consulta para un rol `Auditor` que no existe en el RBAC (Admin/Manager/Cashier/Driver). Si se desea segregación de solo-consulta para el Kardex/reportes, hay que extender el enum de roles + políticas + filtros de UI.
3. **Vista de sesiones por dispositivo (Paso 3)**: hoy la revocación es por usuario (security stamp + `ForceDisconnect`). Un panel con IP/fecha/kick individual requiere una tabla de sesiones activas con detalle de conexión.
4. **Paso 9 debe respetar el régimen multi-moneda vigente**: `PriceBsS` canónico = techo a 2 decimales de `PriceRetailUSD × tasa del día` (8.104), más invalidación de cachés y transacción atómica. Un bulk de precios sin esa regla rompería la "ley" de precios Bs.S en catálogo, carrito, caja y recibos.
5. **Paso 11 (crédito)**: nace con bases nuevas (autorizaciones del Paso 4 para excepciones de sobregiro; snapshots de tasa para el bimonetario), pero debe definir convivencia con **retiros parciales/custodia** y con el **cierre de caja** (las CxC impactan el flujo si hay entregas); persistir la tasa de emisión desde el día uno (inmutabilidad histórica).
6. **Paso 13 (caja chica)**: integrarse al arqueo anclado a sesión física (8.137) y a los adelantos de efectivo (comisión existente) para no duplicar deducciones en el cierre.
7. **Paso 14 (ganancias)**: calcular el costo de mercancía vendida con el `UnitCostUSD` histórico de `StockMovement` (Kardex, 8.177) en lugar de reconstruir costos desde el catálogo actual (respeta inmutabilidad y ahorra un motor nuevo). El neto requiere P11/P12/P13.

---

## 4. Nuevas funcionalidades sinérgicas (alto impacto, tecnología ya integrada)

1. **Devoluciones a proveedor (NC de compra)**: el módulo de facturas + Kardex ya soportan referencia y motivos (`Return`, `Damage`, `Observation`); registrar devoluciones contra la factura cierra el ciclo de compras y corrige costo/existencia con auditoría automática.
2. **Etiquetas de góndola con código de barras**: la impresión (térmica) y los precios Bs.S canónicos ya existen; imprimir etiquetas desde el catálogo y desde la futura edición masiva (P9) es sinergia directa.
3. **Panel gerencial único ("centro de control")**: KPIs del Kardex (P5) + cierres/arqueos + auditoría de autorizaciones (P4) + revocación de sesiones — una vista de supervisión diaria sin nuevas fuentes de datos.
4. **Conteo cíclico express**: el escáner Web ya está operativo; un modo "conteo por categoría" acotado puede preceder al P6 completo y alimentar el Kardex de inmediato (requiere P8 para delimitar).
5. **Alertas proactivas de stock crítico/vencimientos** sobre el Kardex (umbrales configurables) — los datos y la consulta ya existen.

---

## 5. Reordenamiento óptimo propuesto

**Orden actual restante:** 6 → 8 → 9 → 10 → 11 → 12 → 13 → 14 → 15.
**Propuesta (menor esfuerzo / mayor efecto dominó):** **8 → 6 → 9+10 → 15 → 13 → 11 → 12 → 14**

| Movimiento | Justificación |
|------------|---------------|
| **8 (Categorías) primero** | Es la llave estructural: habilita filtros de P9 y agrupación de P14/P15. Dos tablas + UI; esfuerzo contenido. |
| **6 (Conteo ciego) tras P8** | Usa el escáner existente y categorías para delimitar sesiones; deja el stock "verídico" ANTES de tocar precios masivos (no se liquida sobre inventario sucio). |
| **9+10 juntos** | P10 es una política de redondeo dentro del motor de P9; separarlos obliga a reabrir vista previa y DTOs dos veces. |
| **15 antes del crédito** | Liquidar stock muerto primero recupera liquidez y reduce el capital que luego se financiaría a crédito; el puente a P9 ya existe. (Trade-off: si se prioriza fidelidad al orden original, P15 puede volver al final sin bloquear nada.) |
| **13 (caja chica) antes del bloque de crédito** | Bajo esfuerzo, corrige el arqueo y su dato alimenta el neto de P14. |
| **11+12 al final del bloque financiero** | El diseño más pesado; su base (autorizaciones, multi-moneda, clientes) ya está madura. |
| **14 al cierre** | Consume todo lo anterior (P9/10, P11/12, P13) y el Kardex. |

**Dependencias duras que no cambian:** P12 después de P11 · P14 después de P13 · P15 después de P9 · P6 después de P5 (modelo real).

---

## 6. Sugerencias operativas

- El historial sigue el patrón de tramos con ANEXO + verificación independiente: mantenerlo para los pasos grandes (P9, P11, P14).
- Follow-ups vigentes del tramo 8.177 (ANEXO §C): extracción de tipos de `StockKardexViewModel`, `[Obsolete]` del ajuste legado, referencia clicable del Kardex y enriquecimiento de productores tras el WIP de ingesta unificada.
- El WIP sin commitear de ingesta unificada de facturas debe cerrarse antes de nuevos tramos sobre esa vista para evitar entrelazado de hunks.
