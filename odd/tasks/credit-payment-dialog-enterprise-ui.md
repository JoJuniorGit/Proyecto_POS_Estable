# Credit Payment Dialog Enterprise UI

Branch: V0.15. Delivery: local work-unit commits; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`. RDD: off (clone_local).

## Specs

S1. Rebuild the WPF `Registrar Abono CxC` modal (`Desktop.Client/Views/CreditPaymentDialog.xaml` + `CreditPaymentDialogViewModel`) as a 1:1 sibling of `Finalizar Cobro` (`CheckoutDialog.xaml`): white modal, header/footer `#F8FAFC`, container borders `#E2E8F0`, input borders `#CBD5E1` with `#6D28D9` 1.5px focus, primary `#6D28D9`/hover `#5B21B6`, soft accents `#F5F3FF`/`#EDE9FE`, text `#0F172A`/`#64748B`. No legacy underline-only inputs, no heavy `DropShadowEffect`, no bright green for debt, no uppercase button labels.

S2. Uniform horizontal rhythm: header `Padding="24,16"`, body horizontal margin 24 on every band, footer `Padding="24,14"`; the validation banner aligns to the same 24px gutter.

S3. Header: 36x36 `#EDE9FE` badge with a collect icon and title `Registrar Abono CxC` (18px Bold). Context line `{NombreCliente} · {CedulaRif} | Tasa: 1 USD = {Tasa} Bs.S`, hiding the rate segment in `Single`. Clean 32x32 `×` close button with `#E2E8F0` hover.

S4. Unified 3-KPI summary bar exactly like `Finalizar Cobro`: one `#F8FAFC`/`#E2E8F0` container with 3 equal columns and 1px vertical separators. `SALDO PENDIENTE` local-first (`Bs.S` bold 19px, `$` secondary 12px), `ABONO A REGISTRAR` local-first with `#64748B` at zero and `#15803D` above zero plus `$` secondary, and `SALDO RESTANTE` protagonist cell: `#F5F3FF`/`#5B21B6`/`#6D28D9` while balance remains, `#F0FDF4`/`#15803D` plus `✓ Saldado` when zero. Secondary equivalents hidden in `Single`.

S5. `Imputación del abono` as a clean row: 13px SemiBold label left and a `#E2E8F0` segmented control right with `FIFO (más antigua)` and `Factura específica`; active segment `#6D28D9`/white, inactive `#475569`, radius 8/6, no dark shadow. Existing FIFO/explicit commands and RBAC unchanged.

S6. `Datos del abono` card `#F8FAFC`/`#E2E8F0` radius 12 padding `16,14`: header with live conversion top-right (`≈ $ 19,50` for local capture, `≈ Bs.S 18.064,02` for USD capture). Row 1: full-width payment-method ComboBox (white box, `#CBD5E1` border, placeholder `Seleccione método de pago`, hover `#EDE9FE`/`#5B21B6`) plus 215px amount input at `Height 40` with dynamic internal prefix `Bs.S` (default) or `USD` (USD method), right-aligned 15px SemiBold tabular value, select-all on focus/click, strict numeric entry and configured decimal separators. Row 2: full-width `Referencia / comprobante` field (height 38) with dynamic placeholder `"N° de referencia / comprobante (obligatorio)"` vs `"Referencia / comprobante (opcional)"`.

S7. Monetary rule: before a method is selected the capture currency is the local currency (`Bs.S` in Dual), prefilled with `PendingUSD * ExchangeRate` rounded to 2 decimals; live `≈ $` shows the USD equivalent. Selecting a USD method switches the prefix/value to `USD` and the live equivalence to `≈ Bs.S`; switching back to any local method returns cleanly to `Bs.S` at the current rate. `Single` hides rate and secondary equivalents. Exact full-payment in `Bs.S` must liquidate 100% of the pending balance with `Saldo restante == 0,00` and no phantom cents.

S8. `Obligaciones a imputar` card white/`#E2E8F0` radius 12 padding `14,12`: contextual note `"Se aplicará de la factura más antigua a la más reciente."` in FIFO or `"Seleccione la factura específica a la cual aplicar el abono."` in explicit mode. Compact list `MaxHeight 150` with 6px scrollbar; each row padding `10,8` radius 8, hover `#F8FAFC`, selected `#F5F3FF` + `#DDD6FE`; 18x18 vector CheckBox (interactive only in explicit mode), document `F00287` 13px Bold plus discrete status pill (`Vigente` `#F1F5F9`/`#475569`, `En mora` `#FEF2F2`/`#DC2626`) that never collides with the amount, and right-aligned `Bs.S` bold 13px primary with `$` 11.5px secondary (no green debt).

S9. Validation banner above the footer: `#FFFBEB`/`#FDE68A` radius 8 padding `12,8`, info icon plus message in 12px `#B45309`, visible only while a blocking condition exists. Footer `#F8FAFC` top border radius bottom 16 padding `24,14`: neutral `Cancelar` (white, `#CBD5E1`, `#334155`, hover `#F1F5F9`, height 42) left and `Registrar Abono` primary right (height 42, padding 22, sentence case, no dark shadow) disabled to `#E2E8F0`/`#94A3B8` while method, amount, mandatory reference, invoice selection or balance validation blocks it.

S10. Preserve the complete existing contract: every field, FIFO or explicit allocation, validation messages/behavior, `CreditPaymentRequestClientDto` payload (`AmountUSD`, method, reference, receivable ids, idempotency key), `RegisterCreditPaymentAsync`, dialog close/result flow, and existing AutomationIds. Add `Esc` closes/cancels and `Ctrl+Enter` registers only when the form is valid.

S11. Tests: default `Bs.S` open with exact USD conversion; USD method switches capture currency and local method returns to `Bs.S`; full `Bs.S` payment leaves `Saldo restante == 0,00`; `Registrar Abono` is disabled without method, mandatory reference or explicit invoice selection. Keep existing FIFO/explicit/error tests green with updated currency-aware amounts.

S12. Verification: `dotnet build CommandCenter.slnx -c Release` (0 warnings/0 errors), full .NET suite green except the pre-existing PostgreSQL lane without `TEST_POSTGRES_CONNECTION`, and 96/125 DPI visual parity with `Finalizar Cobro` when a WPF harness is available; otherwise report the visual check as pending with build/test evidence.

Assumptions:
- The modal is the WPF `CreditPaymentDialog`; the web `CreditPaymentModal` is out of scope for this change.
- `TodayRate` comes from the receivables list and remains the client-side projection rate; the server still freezes the collection rate.
- In `Single`, "local" is the only configured currency and no secondary equivalent is rendered.

## Tasks

- [x] T1 (S1-S7, S10) - ViewModel: dual input currency, prefill/conversion/clamp, derived display props, CanConfirm, customer/rate context, validation preserved.
- [x] T2 (S1-S9) - XAML: 6-band layout, KPI bar, segmented allocation, payment card, obligations list, validation banner, fixed footer.
- [x] T3 (S10) - Keyboard and input ergonomics: Esc, Ctrl+Enter, select-all on focus/click.
- [x] T4 (S11) - Update and add ViewModel unit tests.
- [x] T5 (S12) - Build, focused tests, full suite, visual parity evidence or pending note.

## Log

L1. User requested a Principal WPF UI/UX & MVVM redesign of the `Registrar Abono CxC` modal as a 1:1 visual sibling of `Finalizar Cobro`, with local-first `Bs.S` capture, automatic `USD` switching only for USD methods, exact full-payment liquidation without rounding residue, unified separators, Single-mode suppression, six visual bands, Esc/Ctrl+Enter, unit tests, Release build and 96/125 DPI visual validation.

L2. Implemented the dual-currency capture model: default local prefill (`suggested USD * TodayRate`), per-method `USD`/local switching with conversion, `AmountUSD` service payload, exact-due clamp for full liquidation, local-first KPI/list presentation, dynamic reference placeholder, `CanConfirm` gate, `Esc`/`Ctrl+Enter`, and select-all input. Preserved FIFO/explicit allocation, warnings, idempotency key, AutomationIds and the `RegisterCreditPaymentAsync` contract.

L3. Evidence: Release build 0 warnings/0 errors; `CreditReceivablesPaymentTests` 10/10; non-PostgreSQL CxC lane 47/47; full .NET suite 4596/4658 with all 62 failures in the PostgreSQL lane missing `TEST_POSTGRES_CONNECTION`; WPF `RenderTargetBitmap` harness screenshots at 96 and 120 DPI (125% scaling) for default, local-ready, USD-method and explicit-allocation states with no clipping or overlap.

