# Hardening WPF (PERF-04/05) + checkout WPF server-authoritative (CLEAN-05)

Objetivo: tramo 8.156 del roadmap de auditoría — PERF-04 (delegados vivos en diálogos), PERF-05 (retención de vistas por DataContextChanged sin Unloaded) y CLEAN-05-WPF (el WPF duplica la liquidación; debe consumir `/checkout-preview` como el Web). Change: `openspec/changes/2026-10-09-audit-wpf-hardening/` (ANEXO 8.156). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (PERF-04)**: `ProductDialog`/`ServerConnectionDialog` con `Action<bool> _closeHandler` almacenado; `-=` en `OnClosed` antes de `Dispose`; sin lambdas suscritas. Detalle: `specs/wpf-view-lifecycle/spec.md`.
- **S2 (PERF-05)**: `InventoryView`/`LoginView` con patrón `DailyClosureView` (hook/unhook en Loaded/Unloaded + DataContextChanged, `_boundViewModel`); `LoginView` no dispone el VM. Detalle: mismo spec.
- **S3 (CLEAN-05 WPF)**: cliente `GetCheckoutPreviewAsync` + DTO + pin de contrato; gate espejo de `computeCheckoutGate` (firma fresca, versión monotónica, fail-closed con mensaje canónico del Web, `RoundingAdjustment` del servidor a CompleteSale, override por `preview.IsFullyPaid`, `RemainingBalance*` del preview cuando fresco); seam determinista de tests. El Web queda intacto (ya conforme desde 8.5-WEB1). Detalle: `specs/wpf-checkout-preview/spec.md`.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1+S2 | delegated (writer) | Vistas WPF: desuscripción en diálogos + ciclo Loaded/Unloaded | pendiente |
| T2 | S3 | delegated (writer) | Cliente preview + gate del CheckoutViewModel + tests | pendiente |
| T3 | S1-S3 | delegated (verify) + inline | Verificación independiente + suite/cobertura + ANEXO 8.156 + cierre | pendiente |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "sigue con 8.156".
- L2 (2026-10-09) — Verificación previa: **PERF-04 CONFIRMADO** (lambdas en `RequestClose` sin `-=` en ambos diálogos); **PERF-05 CONFIRMADO** (hook solo por `DataContextChanged`, sin `Unloaded`; patrón correcto en `DailyClosureView`); **CLEAN-05-WPF CONFIRMADO** (el VM calcula todo local; el cliente WPF NO tiene `GetCheckoutPreviewAsync` — grep 0) y **Web ya conforme** (8.5-WEB1: `computeCheckoutGate` exige firma fresca, bloquea sin validación canónica, usa el rounding del servidor; tests en `CheckoutPreviewGate.test.js`). Bindings del diálogo WPF: `RemainingBalanceLocal/Usd` (L66-67), `ValidationHelperMessage` (L187), `FinalizeSaleCommand` (L212-214).
- L3 (2026-10-09) — Próximo: T1 delegado a writer (superficies: ProductDialog.xaml.cs, ServerConnectionDialog.xaml.cs, InventoryView.xaml.cs, LoginView.xaml.cs).
