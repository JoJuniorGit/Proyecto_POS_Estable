# Hardening WPF (PERF-04/05) + checkout WPF server-authoritative (CLEAN-05)

Objetivo: tramo 8.156 del roadmap de auditoría — PERF-04 (delegados vivos en diálogos), PERF-05 (retención de vistas por DataContextChanged sin Unloaded) y CLEAN-05-WPF (el WPF duplica la liquidación; debe consumir `/checkout-preview` como el Web). Change: `openspec/changes/2026-10-09-audit-wpf-hardening/` (ANEXO 8.156). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (PERF-04)**: `ProductDialog`/`ServerConnectionDialog` con `Action<bool> _closeHandler` almacenado; `-=` en `OnClosed` antes de `Dispose`; sin lambdas suscritas. Detalle: `specs/wpf-view-lifecycle/spec.md`.
- **S2 (PERF-05)**: `InventoryView`/`LoginView` con patrón `DailyClosureView` (hook/unhook en Loaded/Unloaded + DataContextChanged, `_boundViewModel`); `LoginView` no dispone el VM. Detalle: mismo spec.
- **S3 (CLEAN-05 WPF)**: cliente `GetCheckoutPreviewAsync` + DTO + pin de contrato; gate espejo de `computeCheckoutGate` (firma fresca, versión monotónica, fail-closed con mensaje canónico del Web, `RoundingAdjustment` del servidor a CompleteSale, override por `preview.IsFullyPaid`, `RemainingBalance*` del preview cuando fresco); seam determinista de tests. El Web queda intacto (ya conforme desde 8.5-WEB1). Detalle: `specs/wpf-checkout-preview/spec.md`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S2 | delegated (writer) | Vistas WPF: desuscripción en diálogos + ciclo Loaded/Unloaded | hecho — 4 vistas + CreateInvoiceProductDialog (misma clase); build 0/0; focused 12/12; suite 2172/2172; commit 28e32da |
| T2 | S3 | delegated (writer) | Cliente preview + gate del CheckoutViewModel + tests | hecho — RED gate 3F→GREEN 5/5 + pin; combinado 117/117; suite 2178/2178; 8 CheckoutUxTests ajustados; commit 1540a27 |
| T2b | W1/W2/I2 verificación | inline | Mock E2E con preview + test call-path + paridad custodia | hecho — RED→GREEN del mock; focused 16/16; suite 2180/2180; cobertura CON Postgres exit 0; commit 0427309 |
| T3 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.156 + cierre | hecho — PASS WITH WARNINGS (10/10 COMPLIANT; W1/W2/I2 cerrados por T2b; I1 documentado); ANEXO 8.156; commit de cierre en L6 |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "sigue con 8.156".
- L2 (2026-10-09) — Verificación previa: **PERF-04 CONFIRMADO** (lambdas en `RequestClose` sin `-=` en ambos diálogos); **PERF-05 CONFIRMADO** (hook solo por `DataContextChanged`, sin `Unloaded`; patrón correcto en `DailyClosureView`); **CLEAN-05-WPF CONFIRMADO** (el VM calcula todo local; el cliente WPF NO tiene `GetCheckoutPreviewAsync` — grep 0) y **Web ya conforme** (8.5-WEB1: `computeCheckoutGate` exige firma fresca, bloquea sin validación canónica, usa el rounding del servidor; tests en `CheckoutPreviewGate.test.js`). Bindings del diálogo WPF: `RemainingBalanceLocal/Usd` (L66-67), `ValidationHelperMessage` (L187), `FinalizeSaleCommand` (L212-214).
- L3 (2026-10-09) — Próximo: T1 delegado a writer (superficies: ProductDialog.xaml.cs, ServerConnectionDialog.xaml.cs, InventoryView.xaml.cs, LoginView.xaml.cs).
- L4 (2026-10-09) — T1 (28e32da) y T2 (1540a27) completados (T1: 4 vistas + CreateInvoiceProductDialog por la misma clase de defecto; T2: cliente preview + gate espejo con RED 3F→GREEN) + verificación independiente read-only PASS WITH WARNINGS: REQ-WVL-01/02/03 y REQ-WCP-01..05 COMPLIANT; ajustes de CheckoutUxTests auditados sin enmascaramiento; **W1** (el mock E2E no servía /checkout-preview → botón de cobro inhabilitado en modo --e2e), **W2** (sin test del call-path de finalización), **I1** (Variant dialogs: asignación sobre VM local — fuera de la clase; sin cambio), **I2** (checkbox de custodia sin paridad web). Suite CON Postgres 2178/2178; cobertura 0.8886/0.9057/0.8621 (exit 0); web intacto.
- L5 (2026-10-09) — W1/W2/I2 CERRADOS por T2b (0427309): mock E2E con preview canónico + test dedicado (RED: `{}` sin isFullyPaid → GREEN); test `FinalizeSale_SendsServerRoundingAdjustment_ToCompleteSale`; checkbox con `IsEnabled=IsCustodyAllowed`. Suite 2180/2180; cobertura CON Postgres exit 0; build 0/0.
- L6 (2026-10-09) — Cierre: verify-report.md + ANEXO 8.156 + state/tasks actualizados + commit de cierre (hash en git log). Entrega pendiente: push/PR/merge (decisión del mantenedor; cadena stacked-to-main cacheada). Próximo tramo: 8.157 (SEC-07 SignalR) cuando se autorice.
