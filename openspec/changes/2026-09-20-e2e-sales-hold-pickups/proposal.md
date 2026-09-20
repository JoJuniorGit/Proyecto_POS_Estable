# Proposal: E2E Complete Sales Flow, On-Hold Orders & Pending Pickups

## Intent

Fix failing Playwright E2E tests caused by seed credential mismatch and password-change requirement, and implement comprehensive End-to-End test coverage for the complete sales lifecycle in the Web POS client (`Web.Frontend`). This includes standard POS checkout, on-hold orders lifecycle (creation, editing, liquidation, deletion), and pending pickups management.

## Scope

### In Scope
1. **Authentication & Session Robustness (`Web.Frontend/e2e/helpers/auth.js`)**:
   - Create a reusable authentication helper supporting development seed password (`DevAdmin!2026`), handling mandatory password rotation (`mustChangePassword`), and fallback handling.
   - Fix `e2e/auth.spec.js`, `e2e/pos-sale.spec.js`, and `e2e/cash-drawer.spec.js`.
2. **Complete Sales Lifecycle E2E Suite (`Web.Frontend/e2e/sales-flow-complete.spec.js`)**:
   - **Flow A - POS Cart & Checkout**: Search items, quantity adjustments, customer selection, payment split/entry, and receipt confirmation.
   - **Flow B - Cuentas Abiertas (On-Hold Orders)**:
     - Hold current sale (F4 / modal).
     - Navigate to `/pending`.
     - Order editing via `EditSaleModal`.
     - Order checkout and liquidation via `CheckoutModal`.
     - Order cancellation/deletion for unpaid orders via `ConfirmModal`.
   - **Flow C - Retiros Pendientes (Pending Pickups)**:
     - Navigate to `/pickups`.
     - Search and inspect custody orders.
     - Confirm delivery and verify removal from pending list.

### Out of Scope
- Modifying backend core business logic (backend services already implement these features).
- Modifying WPF desktop client (already covered by FlaUI suite).

## Approach

- Use Playwright with resilient locators and explicit waiting for DOM states.
- Encapsulate user authentication in `e2e/helpers/auth.js` to ensure deterministic pre-conditions across all test suites.
- Group tests logically with descriptive assertions following the project's Given/When/Then SDD standards.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Web.Frontend/e2e/helpers/auth.js` | New | Reusable authentication helper with password-change rotation handling |
| `Web.Frontend/e2e/auth.spec.js` | Modified | Fix credentials & assertions |
| `Web.Frontend/e2e/pos-sale.spec.js` | Modified | Fix login precondition |
| `Web.Frontend/e2e/cash-drawer.spec.js` | Modified | Fix login precondition |
| `Web.Frontend/e2e/sales-flow-complete.spec.js` | New | Complete sales, on-hold orders, and pending pickups E2E tests |

## Risks & Mitigations

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Database state variability (e.g. products or seed data) | Med | Use resilient selectors, search for common seed items (e.g. "Harina" or any listed item), or fallback to first available catalog item |
| Password rotation prompt on first login | High | Auth helper explicitly checks for and completes the password change form if displayed |

## Rollback Plan

Revert modified and new spec files in `Web.Frontend/e2e/`.

## Success Criteria

- [ ] `npm run test:e2e` in `Web.Frontend` passes all tests cleanly.
- [ ] No regression in existing frontend unit tests (`npm test` passes 100%).
