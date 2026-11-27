# Web Settings Enterprise UI (WPF-like, Light/Dark)

Branch: V0.15. Delivery: local work-unit commits; no push. Test runner: `cd Web.Frontend && npm test`, `npm run lint`, `npm run build`. RDD: off (clone_local).

## Specs

S1. Unified shell header: icon badge for settings (`Settings` icon, 42-44px, rounded 12, accent-soft background, accent icon), title `Configuración del Sistema` (22px, extrabold, primary text), subtitle `Gestione las preferencias globales, conectividad, métodos de pago y mantenimiento del sistema.` (13px, muted). A subtle right-side meta pill (`Configuración global` with a status dot) on desktop; wraps cleanly on mobile. Keep the existing four sections and RoleGuard.

S2. Navigation parity with WPF (`TabControl` with left strip): sticky rounded panel with `SECCIONES` kicker, `04` counter, four tabs with icon + label + one-line description, hover `surface-soft`, active `accent-soft` + `3px` left accent bar + accent icon/label, keyboard `Arrow/Home/End` contract intact (`role="tablist"`, `role="tab"`, `aria-selected`, `aria-controls`, roving `tabIndex`). Keep the exact section labels: `Moneda, Tasas y Formato`, `Métodos de Pago y Caja`, `Conectividad y Red Local`, `Sistema y Mantenimiento`. Add section descriptions in `settingsSections.js` without changing IDs/labels.

S3. Shared card/section language matching the WPF `SettingsCardStyle` / `SettingsSectionTitleStyle`: surface card, 1px subtle border, radius 14, padding 20 (16 mobile), soft shadow; header row with 36px accent icon badge, title 16px bold, subtitle 12.5px muted, optional status pill on the right. Scoped card headers for every section:
- Currency: `Auto-guardado` accent pill.
- Payments: `N métodos` neutral pill.
- Fee rules: `N reglas` neutral pill + `Nueva Regla` primary button.
- Pairing: `HTTPS activo` success or `Red local` neutral pill + refresh action.
- System: `Acción crítica` warning pill.

S4. Currency section (`SettingsCurrencyFormat`): card header + two selectable format cards (Venezolano Contable / Internacional) with radio, selected accent border/soft background, descriptions and mono examples; live preview panel (`VISTA PREVIA EN VIVO`) with local Bs.S and USD values, separated by a subtle divider. Remove the current inline border/background styles; preserve `handleFormatChange` calls and current values/ARIA. Add an auto-save hint that the format applies across the system.

S5. Payments section:
- `SettingsPaymentMethods` card header + count/status; replace inline cash chips with scoped semantic chips; keep the responsive mobile cards and desktop table (`Nombre`, `Tipo`, `Moneda`, `Requiere Referencia`, `Estado`, `Acciones`). Table headers uppercase 10.5 muted, rows with hover, semantic active/inactive/cash/reference chips, compact delete action; preserve every handler, ARIA label and `Moneda</th>` contract.
- The create form becomes a clean sub-panel (`Nuevo Método de Pago`) with the optional commission callout, 38px fields, CSS grid (no `style={{}}`), checkboxes, and primary `Agregar` button. Keep all validation and API calls exactly.
- `PaymentMethodFeeRules` gets the same card header language, count pill, `Nueva Regla` action, clean form callout and table; preserve Admin-only visibility, test IDs, labels, validation and CRUD.
- No API/endpoint/behavior changes.

S6. Network section (`SettingsPairing`): card header + status pill + refresh; two-column pairing layout: QR panel in a white framed container (QR stays white in dark mode for scannability), adapters/HTTPS callout; manual connection panel with server/hostname, IP copy row, full URL copy row and Wi-Fi note. Use semantic callouts (`info`), mono values, copy success states; keep the existing pairing fetch, QR renderer and clipboard handlers.

S7. System section: `ZONA DE ACCIONES CRÍTICAS` label + warning card with warning icon badge, title `Reiniciar Sistema`, WPF-like description, right-aligned warning action (38-40px) with loading state; keep ConfirmModal and restart API behavior.

S8. Dark and light modes are first-class. Define settings-scoped tokens at `.set-page` (light) and `[data-theme="dark"] .set-page` (dark): surfaces, soft surfaces, borders, text/muted/subtle, accent/hover/soft/contrast, success/danger/warning/info solids and softs, focus ring, shadow. No hardcoded light-only backgrounds/text in any settings component; all new CSS consumes the scoped tokens. Light follows the WPF palette closely (`#F8FAFC`, `#FFFFFF`, `#E2E8F0`, `#CBD5E1`, `#6D28D9`, `#5B21B6`, `#EDE9FE`/`#F5F3FF`, `#0F172A`, `#64748B`); dark uses the web dark tokens/equivalent (surface `#1E293B`, canvas `#0F172A`, border `#334155`, accent `#B39DDB`, dark-on-accent text). Ensure WCAG-safe contrast for primary/warning/danger buttons and chips.

S9. Responsive/accessibility: nav becomes horizontal scroll at <= 980px; hero wraps; cards use 16px padding; currency choices/preview stack; payment tables switch to mobile cards at 640px (existing classes); form grids stack; pairing columns stack; buttons keep >= 34px height and visible focus rings. No clipping/overflow at 390px width.

S10. Preserve all existing web contracts: API calls, handlers, validation, SSR tests, labels, ARIA roles, QR rendering, fee-rule Admin gating, confirmation modals, and the `SettingsPage` test assertion `Moneda</th>`. No new dependencies, no backend changes, no new settings domains.

S11. Verification: `npm test`, `npm run lint`, `npm run build` all green; SSR tests still pass; browser screenshots with Playwright at desktop 1440x1000 light and dark for currency/payments/network/system, and 390x844 light/dark for currency/payments; visual checks for card hierarchy, no light-only hardcoded surfaces, readable chips/inputs, QR white, no overflow, and focus/hover states. If the dev server cannot be started or browser tooling is unavailable, preserve the build/tests evidence and report the visual check as pending.

S12. Constraints: no new libraries, no heavy black shadows, no `#000000` lines, no inline hardcoded palette in settings JSX, no changes outside the web settings surface.

Assumptions:
- A1. "Similar a WPF" is visual/structural parity (shell header, left tab rail, card headers, form field language, critical-zone treatment); the existing four web domains and their features are preserved, not expanded with WPF-only endpoints.
- A2. The web design-system tokens remain the source for dark mode; settings defines a scoped WPF-aligned light palette and equivalent dark values.
- A3. The QR container stays white in both themes by design (scannability).
- A4. Table/mobile response behavior is preserved; no export/API contract is touched.
- A5. The global theme toggle remains the only theme control; the settings page must react to `[data-theme]` without adding a second toggle.

## Tasks

- [x] T1 (S1-S10, S12) - Implement the shared shell, scoped light/dark design system, card headers, currency choices/preview, payment method + fee rule cards, pairing and critical system card; preserve all behavior. Route: delegated writer. Evidence: `npm test` 827/827, lint clean, Vite production build successful.
- [x] T2 (S11) - Parent verification: tests/lint/build, Playwright light/dark desktop/mobile screenshots, contrast/overflow fixes, final work-unit commit. Evidence: 4/4 Playwright visual tests passed at 1440x1000 (light/dark, four sections) and 390x844 (light/dark, currency/payments); fixed mobile preview gap, 2x2 tab rail, mobile fee-rule cards, ARIA orientation and method-name wrapping; primary button contrast computed as white-on-purple in light and #0F172A-on-lavender in dark.

## Log

L1. User request (verbatim):

Mejroa la maximo la vista de la configuracion dew la version WEB HAzla similar a la versiion WPF, cuida ambos modos tanto modo oscuro como claro, la seccion de configuracion

L2. Baseline: the web settings page is a 230px tab rail + four generic `.card` panels; it uses global tokens but several inline light-only colors (currency choice cards, cash/active chips, critical zone, Wi-Fi callout, delete borders, QR frame) that break dark mode. Section components use `card`/generic headings with no WPF-like section header/icon/status language. Existing SSR tests require the four exact labels, one active tab, hidden panels, and the `Moneda</th>` payment table header. Web tests run with Node's test runner + esbuild JSX loader; Playwright 1.51 and Chromium are installed for visual validation.

L3. Design decisions recorded in A1-A5: preserve four domains/feature behavior; scoped WPF-aligned light palette + dark equivalents; no inline palette; QR stays white; one global theme toggle.

L4. T1 executed by `gentle-ai-worker`: added the scoped `--set-*` light/dark token system, enterprise hero + sticky section rail with descriptions/count/status, shared card/header/status language, currency choice cards + live preview, payment method card/table/form, fee-rule card/table/form, pairing QR/connection panels and the critical restart card. All handlers, APIs, validation, ARIA roles, Admin gating, labels and the `Moneda</th>` contract were preserved; no new dependencies. Evidence: `npm test` 827/827, `npm run lint` clean, `npx vite build --outDir dist --emptyOutDir` successful.

L5. Parent visual verification with Playwright + mocked APIs at 1440x1000 and 390x844, light and dark, all four domains. Found and fixed: mobile currency preview items inherited a 180px flex-basis (huge blank gap), mobile rail showed only one section (now a 2x2 tab grid), fee-rule table was cut at mobile (now a mobile card view with all fields), `aria-orientation` now follows the compact breakpoint, and payment/fee method names no longer wrap in desktop tables. Computed primary-button colors: light `rgb(255,255,255)` on purple; dark `rgb(15,23,42)` on lavender. Screenshots are in the temporary harness output, not committed.

L6. Final verification: `npm test` 827/827 (188 suites), `npm run lint` clean, `npx vite build --outDir dist --emptyOutDir` successful (SettingsPage 44.77 kB JS / 22.67 kB CSS), Playwright 4/4 visual tests passed. The console SignalR negotiate errors are expected because the visual harness has no backend; they did not affect rendering. Unrelated ProductPicker WIP in the working tree was not touched.

L7. The settings page still owns only the four existing web domains; no WPF-only endpoint or feature was added. The QR remains white in dark mode for scannability.
