# Cash drawer — one 3-card row per active currency

Branch: `V0.15` · Delivery: work-unit commits on the feature branch · Tests: `dotnet build CommandCenter.slnx -c Release`, targeted `dotnet test`, `npm run lint`, `npm test`, `npm run build`.

## Specs

- **S1 — Una fila por moneda activa.** La Caja de Tienda muestra una fila horizontal por cada moneda activa del régimen: en `Dual`, local/secundaria primero y principal/dura después; en `Single`, solo la primaria. Cada fila tiene tres tarjetas: `EFECTIVO ESPERADO EN CAJA (M)`, `TOTAL INGRESOS (M)` y `TOTAL EGRESOS (M)`.
- **S2 — Invariante nominal por moneda.** Para cada moneda: `Esperado = FondoInicial + IngresosFisicos - EgresosFisicos`. Sin conversiones cruzadas ni equivalencias a otra divisa. Un movimiento USD solo afecta USD; uno Bs.S solo afecta Bs.S.
- **S3 — Fuente backend.** Exponer `CurrencySessionSummaryDto` por moneda con `CurrencyCode`, `CurrencySymbol`, `OpeningBalance`, `TotalPhysicalInflows`, `TotalPhysicalOutflows`, `ExpectedCashBalance` e `IsLocal`. `TotalPhysicalInflows` excluye la apertura; `TotalPhysicalOutflows` incluye CashOut, caja chica neta de reversas, vueltos, reembolsos y adelantos dispensados; se excluye Closing.
- **S4 — Eliminar el bloque redundante.** Quitar `EXISTENCIAS FÍSICAS POR MONEDA` de WPF y Web. La existencia queda integrada en la tarjeta de Esperado de cada moneda. Si hay varios métodos físicos de una moneda, se puede mostrar un desglose compacto/chips dentro de esa tarjeta (opcional).
- **S5 — WPF.** Sustituir las tres tarjetas fijas y el bloque de existencias por un `ItemsControl` de resúmenes por moneda. Cada fila usa el símbolo/código de su divisa y muestra el fondo inicial como subtítulo del Esperado. Conservar los IDs de automatización del E2E en la fila local (`CashDrawer_BalanceText`, `CashDrawer_TotalIncomeText`, `CashDrawer_TotalExpenseText`).
- **S6 — Web.** Replicar la misma disposición de filas de 3 tarjetas por moneda. Preferir `session.currencySummaries` del backend; mantener el helper cliente solo como fallback para respuestas legacy. Eliminar la tabla/bloque duplicado.
- **S7 — Refresco reactivo.** Tras CASH IN, CASH OUT, adelanto, caja chica, venta, devolución o cierre, la sesión se recarga y los resúmenes se recalculan. Sin cálculos cruzados en el cliente cuando el backend ya provee el resumen.
- **S8 — Verificación.** Build WPF 0/0, tests WPF y backend verdes, lint/test/build Web verdes, y E2E de caja sin regresiones.

## Tasks

- [ ] **T1 — Backend: DTO y cálculo por moneda.** Agregar `CurrencySessionSummaryDto`, `CurrencySummaries` en `CashDrawerSessionResponseDto`, builder en `CashDrawerBalanceService`, mapeo en `CashDrawerService` y pruebas. Route: inline.
- [ ] **T2 — WPF: modelo, ViewModel y XAML.** DTO cliente, `CurrencySummaries` en el ViewModel, filas de 3 tarjetas, eliminación del bloque físico, IDs E2E y pruebas. Route: inline.
- [ ] **T3 — Web: filas de 3 tarjetas.** Usar `session.currencySummaries` con fallback, eliminar el bloque/tabla duplicado, CSS y pruebas. Route: inline.
- [ ] **T4 — Verificación y ANEXO.** Build/test WPF, lint/test/build Web, suite backend, ANEXO LIX. Route: inline.

## Log

### L1 — Pedido original (verbatim)

[UI/UX & Caja] Reestructurar el panel de Caja de Tienda para mostrar una fila dedicada por moneda con Efectivo Esperado, Total Ingresos y Total Egresos de la sesión. Tipo de Incidencia: Historia de Usuario / Mejora Arquitectónica (Task). Prioridad: Alta (P1). Componentes: Desktop.Client (WPF), Web.Client, Sales.Module (CashDrawer).

Contexto: la fila superior muestra solo totales en bolívares; el bloque EXISTENCIAS FÍSICAS POR MONEDA muestra tarjetas aisladas y contradictorias. Objetivo: reemplazar la fila única de KPIs y el contenedor redundante por filas completas e independientes por cada moneda activa, cada una con Efectivo Esperado, Total Ingresos y Total Egresos en su moneda nominal.

En Dual: dos filas (local/secundaria primero, principal/dura después). En Single: una fila con la primaria. Eliminar el contenedor redundante; si hay varios métodos físicos por moneda, mostrar chips/desglose compacto dentro del Esperado. Invariante: Esperado(M) = FondoInicial(M) + IngresosFisicos(M) - EgresosFisicos(M), sin conversiones. Backend: `CurrencySessionSummaryDto` con CurrencyCode, CurrencySymbol, OpeningBalance, TotalPhysicalInflows, TotalPhysicalOutflows, ExpectedCashBalance. WPF: ItemsControl de resúmenes por moneda; refresco tras operaciones. Web: misma disposición. DoD: Dual 2 filas, Single 1 fila, sin bloque duplicado, invariante exacta, sin conversiones cruzadas, build/tests verdes.

### L2 — Hallazgos

- Web ya tiene una tabla por moneda (commit 9e3e5ab), pero la especificación pide filas de 3 tarjetas; hay que reemplazarla y eliminar el bloque físico.
- WPF conserva las 3 tarjetas fijas y el bloque `EXISTENCIAS FÍSICAS POR MONEDA`; los E2E leen `CashDrawer_BalanceText` y `CashDrawer_TotalIncomeText`, que deben sobrevivir en la fila local.
- El backend aún no expone `CurrencySummaries`; `CashDrawerBalanceService` ya resuelve perfil, moneda local y saldos por método.
- La moneda local es la secundaria en `Dual` y la primaria en `Single`. La apertura local/primaria está en `OpeningBalanceLocal`/`OpeningBalanceUsd`.
- Los movimientos físicos tienen `CurrencyCode`, `AmountLocal` y `AmountUsd`; las filas legacy sin snapshot deben caer a la moneda local.
- La caja chica reversa es un ingreso compensatorio: debe excluirse de Ingresos y restarse de Egresos para mostrar el neto.
