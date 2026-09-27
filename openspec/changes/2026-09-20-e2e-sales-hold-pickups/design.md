# Design: E2E Sales Flow, On-Hold Orders & Pending Pickups

## Architectural Context

The POS application (`Web.Frontend`) interacts with `Backend.API` via HTTP REST endpoints and SignalR hubs (`ExchangeRateHub`, `SalesHub`). In an E2E testing environment with Playwright, tests must handle:
1. Real authentication tokens (JWT in cookie or local storage) and password rotation policies.
2. Dynamic product catalog and stock availability.
3. Multi-view navigation and modal state management without race conditions.

## Key Design Decisions

### D1: Reusable Authentication Helper (`e2e/helpers/auth.js`)
- **Problem**: Tests in `auth.spec.js`, `pos-sale.spec.js`, and `cash-drawer.spec.js` duplicated login logic with a hardcoded password `Admin123*`, which fails when the backend is initialized with `DevAdmin!2026` or when `MustChangePassword` is active.
- **Decision**: Centralize authentication in `loginAsAdmin(page)`.
  1. If already on `.pos-page`, return immediately.
  2. Attempt login using `DevAdmin!2026` (or `Admin123*` if already rotated).
  3. If password rotation is required (`mustChange = true`), input `Admin123*` in both new password fields and submit.
  4. Wait for `.pos-page` locator with proper timeout.

### D2: Complete Sales Flow Suite (`e2e/sales-flow-complete.spec.js`)
- **Structure**:
  - `describe('Complete Sales Flow')`:
    - Tests adding items, modifying quantity, and checking subtotal.
    - Tests placing order on hold (F4 / button).
  - `describe('Cuentas Abiertas (On-Hold Orders)')`:
    - Tests navigating to `/pending`.
    - Tests expanding order details.
    - Tests editing items in `EditSaleModal`.
    - Tests checkout of on-hold order.
    - Tests cancellation/deletion of unpaid on-hold order.
  - `describe('Retiros Pendientes (Pending Pickups)')`:
    - Tests navigating to `/pickups`.
    - Tests searching and confirming delivery.

### D3: Resilient DOM Selectors & Waiting Strategy
- Avoid brittle CSS selectors that depend on unstable index numbers.
- Use text, role, and semantic class locators:
  - `button:has-text("COBRAR")`, `button:has-text("Editar")`, `button:has-text("Anular")`.
  - Input placeholders: `input[placeholder*="Buscar"]`.
- Use Playwright's auto-waiting assertions (`toBeVisible({ timeout: ... })`) instead of fixed `sleep()` calls where possible.
