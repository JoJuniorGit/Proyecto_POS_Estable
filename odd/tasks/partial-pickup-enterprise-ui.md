# Partial Pickup Dialog — Masterchip UI/UX and strict quantity input

Branch: `V0.15`
Delivery: one local work-unit commit; push is human-owned.
Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release`

## Specs

S1. Window shell: `Width=700`, `MinWidth=640`, `MaxHeight=85%` of owner/work area,
    white body, `#E2E8F0` border, `CornerRadius=14`, soft ambient shadow
    (`BlurRadius=24`, `Opacity=0.16`), no new libraries. Three bands:
    fixed header `#F8FAFC`, body `#FFFFFF`, fixed footer `#F8FAFC`.

S2. Header: 38x38 `#EDE9FE` badge, package icon `#6D28D9`, title
    `Registrar Retiro Parcial` 18 Bold `#0F172A`, subtitle
    `Indique las cantidades físicas que se entregan hoy al cliente.` 12.5
    `#64748B`, invoice pill `Factura N° X · Cliente` when available, 32x32
    close button with `#E2E8F0` hover.

S3. Body: aux note `Las cantidades no pueden superar lo pendiente por producto.`;
    compact `Entregar todo` and `Poner en 0`; product list in a clean card with
    a fixed header and an `ItemsControl` (no DataGrid, no black cell focus),
    list `MinHeight=110`, `MaxHeight=300`, 6px `#CBD5E1` scrollbar without arrows.
    Columns: `PRODUCTO` (`*`, ellipsis + tooltip + fractional badge),
    `PENDIENTE` (amber pill), `ENTREGAR HOY` (115x36 input + `Máx`),
    `QUEDARÍA` (green `✓ Completo`, slate `N pend.`, or red over-delivery).
    Error on a row: `#DC2626` border and `Máximo: X` micro-message.

S4. Notes: `Notas (opcional)` label and one `0 / 300` counter; compact
    `MinHeight=68`, `MaxHeight=100` text box with placeholder and no duplicate
    counter.

S5. Footer: live summary (green full delivery, slate partial, amber empty,
    red invalid) and buttons `Cancelar` plus `Confirmar retiro`; confirm is
    disabled when no quantity is positive or any line is invalid; disabled
    colors `#E2E8F0` / `#94A3B8`.

S6. `QuantityInputBehavior` (attached, reusable):
    - focus/Tab selects all; `PreviewMouseLeftButtonDown` focuses and handles
      the click so the selection survives; a digit replaces the prior value;
      Backspace/Delete may leave the field visually empty.
    - only ASCII digits plus one decimal separator when allowed; block letters,
      spaces, signs and invalid paste.
    - integer products reject `.` and `,`; fractional products accept both,
      prefix `.5` as `0.5`, allow exactly one separator and at most 3 decimals.
    - `LostFocus` normalizes empty to `0`, `1.` to `1`, strips leading zeros.
    - live `QuantityText` validation: over pending -> red row + disabled confirm;
      total delivered must be > 0.
    - `Enter` advances between quantity fields; `Ctrl+Enter` confirms; `Esc`
      cancels.

S7. ViewModel: no API, persistence, inventory or accounting contract changes.
    `Entregar todo`, `Poner en 0`, per-row `Máx`, live `QUEDARÍA` and footer
    summary. Invoice context and notes counter come from existing pickup data.

## Tasks

T1. Add `QuantityInputParser` pure helper and `QuantityInputBehavior`.
T2. Refactor `PartialDeliveryDialogViewModel` + `PartialDeliveryLineViewModel`
    (validation, commands, live summary, notes counter, invoice context).
T3. Rebuild `PartialDeliveryDialog.xaml` and code-behind (3 bands, ItemsControl,
    shortcuts, dynamic max height).
T4. Add/update unit tests for parser, validation, commands, summary and notes.
T5. Build 0/0, run focused/full tests, validate WPF normal/125% states and
    compile a harness screenshot of the modal.
T6. Register ANEXO in `docs/reporte.txt`, commit local work unit.

## Log

L1. User request: Masterchip-level redesign of `Registrar Retiro Parcial`,
    institutional palette, three-band modal, strict reutilizable numeric
    behavior for `ENTREGAR HOY`, live remainder/validation, shortcuts, tests.
L2. Exploration: actual modal is `PartialDeliveryDialog.xaml`; ViewModel is
    `PartialDeliveryDialogViewModel`; `PendingPickupItemDto` has no fractional
    flag, so `AllowsDecimals` falls back to any fractional quantity evidence on
    the line. Existing tests expect clamping; they will be updated to the new
    non-clamping validation contract authorized by the request.
L3. Implemented T1-T4. Focused tests: 42/42. Solution build: 0 warnings,
    0 errors.
L4. WPF harness: normal 700x588, dense 15 products 700x612 with slim scroll,
    error state with red input + disabled confirm. Captures at 96 and 120 DPI.
L5. Full suite: 4553/4615 passed; 62 remaining failures are PostgreSQL lane
    tests without `TEST_POSTGRES_CONNECTION`, not this change.
