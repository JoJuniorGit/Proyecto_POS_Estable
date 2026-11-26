# Paridad completa de Tasa de Cambio WPF/Web

Objetivo: alinear `ExchangeRateView` (WPF) y `ExchangeRatePage` (Web) en copy español, formato de tasa, régimen `Single`, modo solo lectura, historial y estrategia de sincronización, reflejando el comportamiento real del backend.
Branch: `V0.15` (rama actual; push/PR quedan a decisión del mantenedor).
Delivery: 2 work units locales (WPF y Web) + documentación; los cambios previos de `ProductPicker` no forman parte de esta feature.
Test runner: `dotnet build CommandCenter.slnx -c Release`; `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; Web: `npm test`, `npm run lint`, `npm run build` en `Web.Frontend`.

## Specs

- S1. Verificación de sincronización: `BcvExchangeRateJob` está registrado como hosted service en `Backend.API/Startup/ServiceCollectionExtensions.cs` y corre con `BcvSettings:AutoSyncIntervalMinutes` default `120` (2 horas). Un valor `<= 0` desactiva el ciclo periódico. El copy de ambos clientes debe reflejar el comportamiento por defecto real: `Sincronización automática cada 2 horas. También puede sincronizar a demanda o ingresar la tasa del día manualmente.` Badge canónico: `Modo Híbrido`. Se elimina `Modo Manual` como descripción exclusiva.
- S2. Textos canónicos, prioridad español: título `Tasa de Cambio`; sección `TASA VIGENTE`; fecha `Actualizada: ...`; formulario `Nueva tasa del día`; botones `Guardar`, `Sincronizar BCV`, `Reintentar`; historial `Historial de Tasas`; columnas `FECHA`, `TASA`, `VARIACIÓN`, `ESTADO`, `ACTUALIZACIÓN`; estados `Vigente` / `Histórica`; contador `N registros`; alerta `Tasa desactualizada: no se ha registrado una actualización en más de 24 horas. Sincronice con el BCV o ingrese la tasa oficial del día.`; banner `Modo Solo Lectura: Su cuenta tiene permisos de consulta. Los cambios e importaciones están bloqueados.`
- S3. Formato principal unificado: `1 {MonedaPrimaria} = {Tasa a 4dp} {MonedaSecundaria}` (ej. `1 USD = 924,3600 Bs.S`), respetando el formato numérico configurado (Venezuelan/International). WPF usa `CurrencyDisplay.Number(rate, 4)`; Web usa `formatNumberEs(rate, 4)`.
- S4. WPF régimen `Single`: ocultar formulario, sincronización, tasa vigente e historial; mostrar el aviso `El régimen monetario Single no utiliza tasa de cambio: los importes se expresan únicamente en {moneda}. No hay sincronización BCV ni carga manual disponibles.` El aviso debe reaccionar a `MonetaryProfilePresenter`/`CurrencyDisplay.ProfileChanged`.
- S5. WPF estados de historial: `Cargando historial...` mientras carga y `No hay registros de cambios de tasa.` cuando no hay filas. El historial mantiene `FECHA`, `TASA`, `VARIACIÓN`, `ACTUALIZACIÓN`, `ESTADO`.
- S6. Web historial: consumir `GET /api/exchange-rate/history?limit=90` y mapear `date`, `rate`, `updatedAtLocal`; calcular `VARIACIÓN` contra la fila anterior y `ESTADO` (`Vigente` primera fila, `Histórica` resto); mostrar contador `N registros`; resaltar la fila vigente; conservar el límite práctico de 90 filas. No se requiere cambio de DTO/API: el contrato ya expone `Date`, `Rate`, `UpdatedAt`, `UpdatedAtLocal`.
- S7. Acción `Sincronizar BCV` única y junto a las acciones de actualización en ambas interfaces: WPF ya la tiene junto a `Guardar`; Web la mueve al mismo bloque de acciones y no la duplica en otra tarjeta.
- S8. Web modo solo lectura: los roles con acceso a la vista pero sin `CanMutateExchangeRate` (Manager, espejo de WPF) ven `TASA VIGENTE` e historial, con el banner de S2, sin formulario, `Guardar`, `Sincronizar BCV` ni `Reintentar`. Admin conserva escritura; Cashier/Driver siguen bloqueados por `RoleGuard` y por el RBAC del backend (endpoints de escritura/sincronización intactos).
- S9. Pruebas: WPF agrega/actualiza tests de VM/XAML para copy, `Single`, estados de carga/vacío y columnas; Web actualiza `ExchangeRatePage.test.js` para copy, `Single`, Manager solo lectura y tabla de historial. Sin cambios de lógica financiera ni librerías externas.
- S10. Verificación final: `dotnet build CommandCenter.slnx -c Release` (0 warnings / 0 errors), suite `.NET` completa al 100%, `npm test` 100%, `npm run lint` limpio y `npm run build` exitoso.

## Tasks

| ID | Specs | Route | Estado | Commit |
| --- | --- | --- | --- | --- |
| T1 / WU1 | S1-S5, S7, S9 | delegated (WPF writer) | completado | — |
| T2 / WU2 | S1-S3, S6-S9 | delegated (Web writer) | completado | — |
| T3 / docs | S10 | inline | pendiente | — |

## Log

- L1 (2026-11-25): Pedido del usuario, verbatim:
  > Aprobada la **Opción 3 (Paridad completa)** entre `ExchangeRateView` (WPF) y `ExchangeRatePage` (Web). Aplica la unificación siguiendo estas definiciones cerradas para que no queden ambigüedades:
  >
  > 1. **Verificación del Copy Real de Sincronización (Fin de la Contradicción):**
  >    - Inspecciona en el backend si `BcvExchangeRateJob` está activo periódicamente o si el sistema opera estrictamente bajo demanda (`Modo Manual`).
  >    - Refleja exactamente el comportamiento real en el subtítulo/badge de **ambos clientes** (WPF y Web) sin contradicciones entre sí.
  >
  > 2. **Textos Canónicos y Formato Numérico Unificado:**
  >    - Alinea todos los títulos, etiquetas, columnas, alertas (>24h) y botones entre WPF y Web usando tu tabla de español canónico recomendado:
  >      * Título: `Tasa de Cambio` | Tasa actual: `TASA VIGENTE` | Fecha: `Actualizada: ...`
  >      * Formulario: `Nueva tasa del día` | Botones: `Guardar`, `Sincronizar BCV`, `Reintentar`
  >      * Historial: `Historial de Tasas` con columnas `FECHA`, `TASA`, `VARIACIÓN`, `ESTADO` (`Vigente` / `Histórica`) y `ACTUALIZACIÓN`.
  >    - Estandariza la visualización principal en ambos clientes al formato **`1 {MonedaPrimaria} = {Tasa a 4dp} {MonedaSecundaria}`** (ej. `1 USD = 924,3600 Bs.S`) respetando el formato numérico configurado en Ajustes.
  >
  > 3. **Paridad de Régimen `Single` y Estados de Carga/Vacío:**
  >    - Agrega en **WPF** el mismo estado de régimen `Single` que ya existe en Web: oculta el formulario manual y la sincronización BCV, mostrando el aviso explicativo de moneda única.
  >    - Agrega en **WPF** los estados `Cargando historial...` y estado vacío limpio (`No hay registros de cambios de tasa.`) equivalentes a los de Web.
  >
  > 4. **Paridad del Historial en Web y Contrato API:**
  >    - Actualiza el contrato/DTO del historial si falta algún campo para que **Web** muestre `FECHA`, `TASA`, `VARIACIÓN`, `ESTADO` (píldoras `Vigente` / `Histórica`), `ACTUALIZACIÓN`, el contador `N registros` y el resaltado de la fila vigente, ampliando el límite de 10 filas en Web a un historial práctico (ej. 60–90 registros o paginado, sin romper performance móvil).
  >    - Mantén el botón `Sincronizar BCV` integrado coherentemente junto a las acciones de actualización en ambas interfaces (sin duplicarlo en dos tarjetas distintas).
  >
  > 5. **Estrategia Unificada de Solo Lectura (RBAC):**
  >    - Alinea **Web** con **WPF**: permite a los usuarios autenticados sin permiso `CanMutateExchangeRate` visualizar la tasa vigente y el historial en **Modo Solo Lectura** con el banner informativo, ocultando el formulario manual y `Sincronizar BCV` (manteniendo intacta la protección RBAC del backend en los endpoints de escritura/sincronización).
  >
  > 6. **Verificación:**
  >    - Actualiza los tests unitarios/componentes de WPF y Web cubriendo paridad de textos, régimen `Single`, modo solo lectura y columnas del historial.
  >    - Verifica `dotnet build CommandCenter.slnx -c Release` (`0 warnings / 0 errors`), suite `.NET` en verde, y `npm test` + `npm run lint` + `npm run build` limpios en Web.

- L2 (2026-11-25): Evidencia de verificación inicial:
  - `BcvExchangeRateJob` registrado en `Backend.API/Startup/ServiceCollectionExtensions.cs:262` con `AddHostedService`.
  - `Backend.API/appsettings.json` define `BcvSettings:AutoSyncIntervalMinutes = 120`; `appsettings.Production.json` no lo sobreescribe. `ExecuteAsync` desactiva el ciclo cuando el valor es `<= 0`.
  - El contrato `ExchangeRateHistoryResponseDto` ya expone `Date`, `Rate`, `UpdatedAt` y `UpdatedAtLocal`; Web puede calcular variación/estado sin cambio de backend.
  - `UserSession.CanMutateExchangeRate` es `Admin` únicamente; `WpfNavigationPolicy` permite Exchange a Admin y Manager. Web debe mantener el mismo par de acceso.

- L3 (2026-11-25): **WU1 COMPLETADA**. `ExchangeRateView.xaml` quedó con `Modo Híbrido`, subtítulo de auto-sync cada 2 horas, alerta >24 h canónica, banner de solo lectura, aviso `Single`, estados `Cargando historial...` / `No hay registros de cambios de tasa.` y las cinco columnas del historial. `ExchangeRateViewModel` expone `IsSingle`, `SingleModeNotice`, `IsHistoryLoading`, `HasHistoryRecords` y `HasNoHistoryRecords`; se suscribe a `CurrencyDisplay.ProfileChanged` y se desuscribe en `Dispose`; en `Single` no consulta tasa/historial y limpia filas (incluido un fetch en vuelo); al volver a `Dual` recarga. Los estados de guardado/sincronización quedaron `Tasa de cambio actualizada correctamente.` y `Tasa sincronizada con BCV: {4dp}`. Tests WPF: `ExchangeRateViewModelCommandTests`, `ExchangeRateHistoryRowViewModelTests`, `ExchangeRateViewModelParityTests` y `ExchangeRateViewParityTests`; focused 132/132. Build Release 0/0.
- L4 (2026-11-25): **WU2 COMPLETADA**. `ExchangeRatePage` usa `Tasa de Cambio`, `Modo Híbrido`, subtítulo de auto-sync, `TASA VIGENTE`, `Actualizada:` en `America/Caracas`, `1 {primaria} = {tasa 4dp} {local}`, `Nueva tasa del día`, `Guardar` + `Sincronizar BCV` en la misma fila, `Historial de Tasas` con `FECHA/TASA/VARIACIÓN/ACTUALIZACIÓN/ESTADO`, píldoras `Vigente`/`Histórica`, contador `N registros`, resaltado de fila vigente y límite de 90 filas. Manager ve el banner de solo lectura y conserva tasa/historial; Admin conserva escritura; `Single` muestra el aviso y oculta todo el bloque de tasa. Se exportaron `buildHistoryRows`, `ExchangeRateHistory`, `formatCaracasUpdatedAt`, `HISTORY_LIMIT` y `EXCHANGE_RATE_HISTORY_ENDPOINT` para tests SSR. Web: focused 12/12, `npm test` 826/826, `npm run lint` limpio y `npm run build` exitoso.
- L5 (2026-11-25): **VERIFICACIÓN FINAL (S10)**. `dotnet build CommandCenter.slnx -c Release`: 0 advertencias / 0 errores. Suite `.NET` completa con PostgreSQL real (`TEST_POSTGRES_CONNECTION` sobre `CommandCenterDb_test`): **4512/4512**. Web: `npm test` **826/826**, `npm run lint` sin hallazgos, `npm run build` exitoso (regeneró `Backend.API/wwwroot`, assets no stageados). Los cambios previos de `ProductPicker` siguen fuera del alcance y sin stagear.
