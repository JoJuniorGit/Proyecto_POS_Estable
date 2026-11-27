# Credit Receivables: Paid Debts History (CxC)

Branch: V0.15. Delivery: one work-unit commit per WU; no push until WU3 closes. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`; web `npm test`, `npm run lint`, `npm run build`.

## Specs

S1. Pending portfolio isolation: `Deudas Pendientes` only returns open obligations with balance > 0.005 USD (Vigente/Parcial/Vencida); paid and cancelled debts never contaminate the operational table, totals or KPIs.

S2. Backend paid history: `GET /api/receivables/paid` returns paginated paid obligations with historical realized totals, paid-at date, days to settle, on-time flag, payments count, methods summary and a period `Summary` block.

S3. Backend obligation timeline: `GET /api/receivables/{obligationId}/payments` returns the ascending payment timeline with frozen historical rate, paid currency, credited USD, imputation mode, cashier, running balance after each payment and Aplicado/Anulado status. Works for paid and partial obligations.

S4. Backend flat payments: `GET /api/receivables/paid/payments` returns paginated payments of paid obligations with their allocations, so a FIFO payment that settled multiple invoices counts once in the totals.

S5. WPF: two clean sub-sections (`Deudas Pendientes`, `Deudas Pagadas`) with live counters; pending keeps risk KPIs and only active status options; paid gets filters, four realized KPIs, invoice/payment modes, pagination and a right slide-over timeline with Esc/close/backdrop, previous/next navigation and historical amounts. A settled payment removes the invoice from pending and offers `Ver en Pagadas`.

S6. Web: same two sub-sections, paid filters, invoice/payment modes, realized KPIs and timeline drawer with historical rates; pending excludes paid invoices.

S7. Single/Dual and RBAC parity: Single hides rates and secondary equivalents; existing Admin/Manager/Cashier read permissions remain.

S8. Tests: backend isolation, frozen rates/running balance, FIFO no double counting, annulled-payment exclusion, Single/RBAC; WPF/web tab, settlement transition, mode switch, Esc drawer and Single hiding.

S9. Verification: Release build 0/0; .NET suite green except the known PostgreSQL lane without connection; web test/lint/build clean; WPF visual check at 96/120 DPI with no paid invoice in pending.

## Tasks

- [x] WU1-T1 (S1) - Default open-only filter in the Postgres and memory receivable queries + regression test.
- [x] WU1-T2 (S2-S4) - Core DTOs, service queries and `ReceivablesController` endpoints for paid list, timeline and flat payments; client DTOs and `ISalesService` transport.
- [x] WU1-T3 (S5) - Export CSV/XLSX/PDF and paid cancellation PDF.
- [x] WU2-T1 (S5) - WPF view model state, filters, KPIs, modes, pagination and settlement transition.
- [x] WU2-T2 (S5) - WPF segmented sub-sections, paid tables and timeline slide-over.
- [x] WU2-T3 (S8) - WPF unit tests.
- [x] WU3-T1 (S6) - Web paid sub-section, modes, timeline drawer and client exports.
- [x] WU3-T2 (S7/S8) - Web tests and Single/RBAC coverage.
- [x] WU3-T3 (S9) - Build, .NET suite, web checks, visual validation.

## Log

L1. User requested separating paid debts from pending in CxC, with a dedicated `Deudas Pagadas` sub-section, full auditable payment history, strict realized vs estimated amounts, WPF + web parity, TDD and 3 work units.

L2. WU1 backend implemented: open-only default portfolio, `PaidReceivablesListDto`/`ReceivablePaymentTimelineDto`/`PaidReceivablePaymentsListDto` contracts, `CreditService.Paid` queries with frozen payment snapshots, `ReceivablesController` routes and client transport. Evidence: Release build 0/0; new paid tests 5/5; existing CxC list tests 14/14 after updating the isolation expectation.

L3. WU2 WPF implemented: segmented Deudas Pendientes/Deudas Pagadas with live counters, open-only status options, paid filters/presets, invoice/payment modes, 4 realized KPIs, paginated tables, settlement notice with Ver en Pagadas, per-obligation slide-over with frozen rates/running balance and Esc/backdrop/close. CSV export works; Excel/PDF and the cancellation PDF remain for WU3. Evidence: Release build 0/0; paid WPF tests 3/3; CxC suite 55/56 with only the PostgreSQL-lane environment failure; harness screenshots show paid section, pending section and timeline slide-over.

L4. WU3 Web implemented: same Deudas Pendientes/Deudas Pagadas segmented sections, open-only status options, paid filters/presets, invoice/payment modes, realized KPIs, paginated tables and a right-side timeline drawer with frozen rates and Esc/backdrop close. CSV/Excel/print-PDF exports are client-side; the backend PDF constancia and server-side exports remain pending. Evidence: 
pm test 829/829, 
pm run lint clean, 
pm run build clean; .NET Release build 0/0. WPF Excel/PDF export and server-side historical exports remain open.

L5. Final evidence: Release build 0/0; .NET suite 4608/4670 with only the 62 known PostgreSQL-lane environment failures and no unrelated regressions; 
pm test 829/829, 
pm run lint clean, 
pm run build clean; WPF harness at 96 and 120 DPI for pending, paid and timeline states (paid invoices absent from pending). Pending: server-side CSV/XLSX/PDF export and per-debt cancellation PDF; annulled-payment status requires a data-model field not present today.

L6. Brechas cerradas: ReceivablesExportService server-side CSV/XLSX/PDF y ReceivablesPdfGenerator para la constancia de cancelación; endpoints paid/export y {id}/payments/pdf; WPF y web consumen esos binarios. Se agregó IsVoided/VoidedAtUtc/VoidReason a ReceivablePayment con migración AddReceivablePaymentVoiding; los totales y KPIs excluyen abonos anulados y el timeline los conserva como Anulado. Evidencia: build 0/0; .NET 4612/4674 con solo 62 fallos PostgreSQL sin TEST_POSTGRES_CONNECTION; web 829/829, lint/build clean.

