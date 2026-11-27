# Auditoría financiera bimonetaria — remediación pre-Paso 14

Objetivo: cerrar las desviaciones detectadas en la auditoría de consistencia matemática, contable y bimonetaria (gaveta dual, Reporte Z, reembolsos CxC, proyecciones WPF, idempotencia y contrato de precisión de tasa) sin alterar la semántica regular de ventas.
Arquitectura: el libro `CashTransaction` es la fuente de verdad física; el Reporte Z reconcilia cobros registrados (`SalePayment`/`ReceivablePayment`) más movimientos físicos no duplicados. La sesión de caja conserva saldos separados Bs.S/USD y los transfiere al rollover.
Rama: V0.15 — Entrega: work units locales; push/PR a decisión del mantenedor — Runner: `dotnet build CommandCenter.slnx -c Release`, `dotnet test` focalizado y suite, `npm test`/`npm run lint` si toca Web.

## Specs

S1 — Gaveta dual real en apertura y rollover. El saldo físico USD debe sobrevivir al cierre de turno y a la apertura automática. `OpenSessionRequest` acepta `OpeningBalanceUsd`; `OpenSessionAsync` lo persiste; el rollover y la creación automática de sesión transfieren el saldo USD además del local. Prohibido que `GetCurrentBalanceLocalAsync` agregue movimientos USD como si fueran Bs.S.

S2 — Atribución del vuelto por moneda física. El vuelto se entrega en Bs.S (contrato actual `changeBsS`), por lo que el `CashTransaction` de egreso debe atribuirse al método de pago `IsCash && Currency == Bs.S`, nunca al primer método cash del pago original.

S3 — Reporte Z desde movimientos fechados. El esperado por método incluye: `SalePayment` por `CreatedAt` real del pago (incluye apartados `OnHold`), `ReceivablePayment` por `ReceivedAtUtc`, y movimientos físicos no duplicados del libro: vueltos (`SalePayment` Expense), `CreditRefund`, `CashAdvance`, `PettyCashExpense`, `PettyCashReversal`, `CashIn`, `CashOut`, `ManualAdjustment`. Se excluyen `Opening`/`Closing` y los ingresos ya representados (`SalePayment` Income y `CreditPayment`). Movimientos sin método se atribuyen al primer método efectivo. El bloque OPEX sigue siendo informativo y solo suma `PettyCashStatus.Active`, sin doble resta.

S4 — Reembolso de crédito protegido. `CreditRefund` físico toma advisory lock de la sesión después del lock por cliente, valida liquidez local antes de asentar y elige un método cash de la moneda local para la salida; si el último pago fue no-cash, el reembolso no se asienta como físico en ese método.

S5 — Proyección WPF alineada al canal Detal. La edición masiva mantiene el invariante `PriceUSD == PriceRetailUSD` cuando cambia el precio Detal. POS, variantes y editor de pedidos calculan el Bs.S efectivo con el USD retail y la regla cash del artículo, no con un `PriceUSD` potencialmente obsoleto ni con techo canónico ciego.

S6 — Idempotencia obligatoria y replay sin tasa viva. `POST /api/sales/credit-payments` y `POST /api/cashdrawer/petty-cash` exigen `Idempotency-Key`. El replay de caja chica se resuelve antes de leer la tasa BCV vigente.

S7 — Precisión decidida: tasas operativas con techo a 2 decimales. Se conserva `RoundingCalculator.RoundExchangeRateCeiling(rate) = ceil(rate*100)/100` como única cuantización operativa, aunque las columnas persistan `numeric(18,4)`. Debe quedar cubierto por pruebas de contrato explícitas.

## Tasks
- T1 | S1, S2 | Gaveta dual: apertura USD, rollover y balance local estricto, atribución del vuelto | inline | done | commit: 8857ac0
- T2 | S3 | Reporte Z: cobros OnHold, libro físico completo, sin doble cómputo | inline | done | commit: ce70cf3
- T3 | S4 | CreditRefund: lock de sesión, liquidez y método/currency del reembolso | inline | done | commit: d160be4
- T4 | S5 | WPF: invariante retail en edición masiva, POS, variantes y editor de pedidos | inline | done | commit: 2cdebf0
- T5 | S6 | Idempotencia obligatoria CxC/caja chica y replay sin tasa | inline | done | commit: 6655baf
- T6 | S7 | Contrato de tasa 2dp con techo: pruebas explícitas | inline | done | commit: 6655baf
- T7 | S1-S7 | Build Release, suites, evidencia y ANEXO | inline | done | commit: docs(8.200) + ANEXO XXXIV

## Log
- L1 (verbatim — autorización del mantenedor): "aplica el plan quirurgico. con 2dp con techo"
- L2 (2026-10-25): **T1-T7 COMPLETADAS**. Decisión de precisión: se conserva `RoundExchangeRateCeiling` con techo a 2 decimales como única cuantización operativa (las columnas 18,4 no implican 4dp de cálculo). Entregado: gaveta dual real (apertura USD, rollover de ambos buckets, saldo local estricto y vuelto atribuido a Bs.S); Z reconstruido desde `SalePayment.CreatedAt` (OnHold incluido) + libro físico no duplicado (vueltos, CreditRefund, CashAdvance, CashIn/CashOut/ManualAdjustment) y caja chica Active una sola vez; reembolso CxC con lock de sesión, liquidez por moneda y ruteo a efectivo local si el último pago fue no-cash; WPF con invariante `PriceUSD == PriceRetailUSD` y precio efectivo por canal Detal; `Idempotency-Key` obligatorio en abonos/caja chica y replay antes de la tasa; pruebas explícitas de tasa 2dp. Evidencia: build Release 0/0; focalizados 645/645; nuevos `FinancialAuditRemediationTests` 12/12; suite sin carril Postgres 3522 superados con 14 fallos ambientales/pre-existentes (28P01 del carril Postgres y DI de OcrExtraction); WPF E2E 7/7; Web sin cambios. Residuales: UI de apertura sin campo USD (backend/API y rollover sí), clasificación de moneda por nombre (8.9-M16), carril Postgres real no ejercitado.
