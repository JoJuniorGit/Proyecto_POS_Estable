# Caja Chica — subsección dedicada dentro de Caja

Objetivo: separar Caja Chica del flujo de movimientos de caja, manteniéndola dentro del módulo Caja como una subsección propia, con resumen del turno, listado, acciones y estética consistente en Web y WPF.
Branch: `V0.15` (work unit local; commit/push pendientes de autorización).
Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`; Web: `npm test`, `npm run lint`, `npm run build`.

## Specs

- **S1 — Subsección dentro de Caja.** La vista de Caja debe exponer una navegación secundaria clara entre `Movimientos` y `Caja Chica`, sin sacar la caja chica del módulo Caja. La sección activa debe ser visible y persistir mientras la sesión de caja esté abierta.
- **S2 — Caja Chica dedicada.** Al seleccionar `Caja Chica`, el contenido de movimientos/últimos ingresos se oculta y se muestra únicamente el panel dedicado: resumen del turno, saldos físicos autorizados, listado completo, acciones de registrar, refrescar, descargar vale y anular.
- **S3 — Resumen detallado.** Mostrar como mínimo: gastos activos, total activo en moneda local, total activo en USD (solo Dual) y gastos anulados. Mantener el arqueo ciego: los saldos físicos solo se muestran a Admin/Manager.
- **S4 — Volver a Movimientos.** El botón de cierre del panel y la pestaña `Movimientos` deben devolver a la vista normal de caja. El botón `CAJA CHICA` del encabezado debe abrir/seleccionar la subsección, no alternar de forma ambigua.
- **S5 — Estética y responsive.** Usar los tokens, cards, chips, tipografía y espaciados existentes. Web debe funcionar en móvil/desktop; WPF debe respetar MaterialDesign, alturas y virtualización.
- **S6 — Sin cambios de contrato.** No se modifican endpoints, snapshots, autorización, arqueo ni políticas de caja chica. Solo UI/ViewModel y tests.

## Tasks

- T1 | S1/S2/S5 | web | Navegación secundaria y render dedicado de Caja Chica; ocultar movimientos cuando está activa. | completed
- T2 | S3/S5 | web | Resumen del turno, filtros/búsqueda y estética del panel dedicado. | completed
- T3 | S1/S3/S4/S5 | wpf | Subsección, resumen, visibilidad de movimientos y comandos de navegación. | completed
- T4 | S6 | verify | Tests Web/WPF, build Release, lint y ANEXO. | in progress — PostgreSQL lane pending

## Log

- L1 (2026-11-15): pedido verbatim del mantenedor:

  > Necesito separa la caja chica a un vista dedicada a caja chica, puedes mantenerlo dentro decaja pero con una subseccion dedicado a caja chica, hazlo lo mejor posible detallado y manten la estetica del sistema, esfuerzate al maximo hermano


- L2 (2026-11-15): **T1-T3 COMPLETADAS (work unit local, sin commit)**. Web: subnav Movimientos/Caja Chica, panel dedicado con KPIs de activos/totales/anulados, filtros y búsqueda; se oculta el contenido de movimientos al activar la subsección. WPF: pestañas Movimientos/Caja Chica, tarjeta blanca con header, chips de filtro, búsqueda, KPIs, saldos, listado virtualizado y comandos de retorno; los movimientos se colapsan con la subsección activa. Evidencia: Web 805/805, lint limpio y build exitoso; WPF build 0/0, regresión de caja 22/22 (incluye la prueba nueva de subsección); ANEXO LXVIII registrado. Pendiente: suite .NET completa con PostgreSQL, inspección visual WPF y commit/push.
