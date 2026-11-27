# P0 — Comisiones de medios de pago y gastos operativos mensuales

Objetivo: configurar las comisiones reales del punto de venta, permitir ingresar la comisión al crear un método de pago y llevar los cargos mensuales como gasto fijo prorrateado en rentabilidad.
Branch: `V0.15` (work unit local; commit/push pendientes de autorización).
Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj`; Web: `npm test` + `npm run lint` + `npm run build`.

## Specs

- **S1 — Comisión en el alta de métodos de pago.** El formulario de creación de métodos de pago debe incluir un apartado opcional de comisión: tipo (`Percentage`, `Fixed`, `PercentagePlusFixed`), porcentaje, monto fijo por transacción, moneda (USD o moneda local) y vigencia desde. Al crear un método digital con comisión mayor que cero, el backend debe crear el método y su `PaymentMethodFeeRule` activa en una sola operación lógica. Un método con 0% o un método de efectivo no crea regla; la ausencia de regla significa costo cero. Si la regla no se puede crear, el alta falla y no debe quedar un método huérfano.
- **S2 — Tasas reales por transacción.** TDD: `0.75%`. TDC: `2.50%`. BioPago: `2.70%`. Transferencia: `0%`. Pago Móvil: `0%`. Efectivo en Bs.S: `0%`. Divisas: `0%`. TDD y TDC deben ser métodos separados porque `PaymentMethodFeeRule` es por método; no se puede aplicar 0.75% y 2.50% al mismo `PaymentMethodId`. No se inventan tasas ni se recalculan cobros históricos.
- **S3 — Gastos mensuales.** El mantenimiento de cuenta de TDC es `$1.60` mensual y los gastos mensuales del POS son `$41.12` mensual. Se modelan como `OperatingExpense` recurrente mensual en USD, con vigencia y asociación opcional a TDC; no son `FixedAmount` por transacción. Se incluyen prorrateados por días calendario activos en la ventana del reporte, tanto en el bloque Devengado como en el Percibido, como `OperatingExpenses`, y se restan de `NetProfit`. No se prorratean por producto ni por método cuando hay filtros; se documenta como gasto global.
- **S4 — Semilla idempotente.** En el arranque se aseguran los métodos `Tarjeta de Débito (TDD)`, `Tarjeta de Crédito (TDC)` y `BIOpago`, sus reglas activas `0.75%`, `2.50%` y `2.70%`, y los dos `OperatingExpense` mensuales. La semilla no renombra, borra ni reescribe métodos históricos; si ya existe una regla activa para un método, no se sobrescribe. Se puede ejecutar dos veces sin duplicar filas.
- **S5 — RBAC y contrato.** El alta de métodos y la configuración de comisiones sigue siendo Admin. La comisión es costo del negocio y nunca se suma al cliente ni al checkout. La UI de Manager/Cashier no puede modificar comisiones. Los DTOs de rentabilidad y exportación deben exponer `OperatingExpenses` sin romper los campos existentes.
- **S6 — Verificación.** Build Release 0/0, migración aplicada dos veces sin drift, tests focalizados de creación de método+regla, semilla idempotente, prorrateo mensual, rentabilidad y RBAC. La suite completa requiere `TEST_POSTGRES_CONNECTION`; si no está disponible, se reporta como bloqueo ambiental.

## Tasks

- T1 | S3 | backend | Crear entidad `OperatingExpense`, configuración EF, DbSet y migración. | completed — `20261115190249_AddOperatingExpensesRevP0`
- T2 | S1/S2 | backend | Extender el alta de métodos con comisión y crear la regla transaccional; validar 0%/efectivo. | completed
- T3 | S2/S4 | backend | Semilla idempotente de TDD/TDC/BioPago, reglas y gastos mensuales. | completed
- T4 | S3/S5 | backend | Integrar `OperatingExpenses` prorrateado en DTOs, rentabilidad, exportación y tests. | completed
- T5 | S1/S5 | web | Agregar apartado de comisión al formulario de alta y mostrar `OperatingExpenses` en rentabilidad. | completed
- T6 | S1/S5 | wpf | Agregar diálogo de alta con comisión y mostrar `OperatingExpenses` en rentabilidad. | completed
- T7 | S6 | verify | Build, tests focalizados, migración/drift, ANEXO y reporte. | in progress — PostgreSQL lane pending

## Log

- L1 (2026-11-15): pedido verbatim del mantenedor:

  > Para P0 ten en cuenta lo siguiente para el punto de venta una comision del 0.75% para TDD y 2.50% TDC + 1.60$ por el mantenimiento de cuenta + "41.12"$ de gastos mensuales del POS. para transferencia 0%, Pago Movil un 0% de comision, para efectivo en y Divisas un 0% y BIOpago un 2.70% y en la creacion de metodos de pago crea un apartado para ingresar la comision asociada al metodo de pago

- L2 (2026-11-15): decisión del mantenedor: **A**. Los `$1.60` y `$41.12` se modelan como gasto fijo mensual separado e incluido en rentabilidad. Las comisiones por transacción quedan limpias: TDD 0.75%, TDC 2.50%, BioPago 2.70%, resto 0%. TDD/TDC/BioPago se crean como métodos separados; no se reescriben métodos históricos.

- L3 (2026-11-15): **T1–T6 COMPLETADAS (work unit local, sin commit)**. Backend: `OperatingExpense` + migración aditiva `20261115190249_AddOperatingExpensesRevP0`; alta de método con `Commission` y regla transaccional; rollback compensatorio; semilla idempotente TDD 0.75/TDC 2.50/BIOpago 2.70 y gastos USD 1.60/41.12; prorrateo por día calendario en Devengado, Percibido y serie; export CSV/XLSX/PDF. UI Web: formulario de alta con comisión y KPI `GASTOS OPERATIVOS`; UI WPF: `PaymentMethodCreationDialog` con comisión y KPI `Gastos Operativos`. Evidencia: build Release 0/0; focalizados ampliados .NET 99/99 (23 nuevos P0); Web 804/804, lint limpio y build exitoso; WPF build 0/0 y ViewModel 2/2; drift/PendingModelChanges verdes. Pendiente: carril PostgreSQL real (`TEST_POSTGRES_CONNECTION`), ANEXO LXVII ya registrado, commit/push sin autorización.


