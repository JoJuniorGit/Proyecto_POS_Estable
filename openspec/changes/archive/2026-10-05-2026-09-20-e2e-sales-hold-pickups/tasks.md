# Tasks: E2E Sales Flow, On-Hold Orders & Pending Pickups

## Phase 1: Authentication & Test Fixes
- [x] **1.1** Create `Web.Frontend/e2e/helpers/auth.js` with `loginAsAdmin` supporting `DevAdmin!2026` and `mustChangePassword` rotation (covers Scenario 1.1).
- [x] **1.2** Update `Web.Frontend/e2e/auth.spec.js` using the new auth helper and verifying both login scenarios (covers Scenario 1.1).
- [x] **1.3** Update `Web.Frontend/e2e/pos-sale.spec.js` and `Web.Frontend/e2e/cash-drawer.spec.js` to use `loginAsAdmin` in `beforeEach` (covers Scenario 1.1).

## Phase 2: Complete Sales Flow & On-Hold Orders Tests
- [x] **2.1** Implement POS sales flow test in `Web.Frontend/e2e/sales-flow-complete.spec.js`: product search, quantity update, customer modal, checkout trigger (covers Scenario 2.1).
- [x] **2.2** Implement On-Hold order creation test: hold cart from POS and verify cart reset (covers Scenario 3.1).
- [x] **2.3** Implement On-Hold order edit test in `/pending`: open `EditSaleModal`, modify items, save changes (covers Scenario 3.2).
- [x] **2.4** Implement On-Hold order checkout test in `/pending`: open `CheckoutModal` and complete liquidation (covers Scenario 3.3).
- [x] **2.5** Implement On-Hold order cancellation test in `/pending`: select unpaid order, trigger cancellation, confirm modal (covers Scenario 3.4).

## Phase 3: Pending Pickups Tests
- [x] **3.1** Implement Pending Pickups tests in `Web.Frontend/e2e/sales-flow-complete.spec.js`: navigate to `/pickups`, search, and confirm delivery (covers Scenario 4.1).

## Phase 4: Verification & Execution
- [x] **4.1** Run `npm test` in `Web.Frontend` to ensure no regression in existing unit tests.
- [x] **4.2** Run `npm run test:e2e` in `Web.Frontend` and verify all tests pass.
