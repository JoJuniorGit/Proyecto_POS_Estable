# Specification: E2E Sales Flow, On-Hold Orders & Pending Pickups

## Overview
Defines the required behavior and assertions for the Playwright E2E test suite covering POS sales, on-hold order lifecycle, and pending pickups.

---

## Requirement 1: Resilient Authentication
The test suite MUST authenticate successfully using the development seed credentials or pre-configured credentials, and MUST handle the mandatory first-time password rotation if presented.

### Scenario 1.1: Valid Login with Password Rotation Handling
- **GIVEN** a browser session at `/` displaying the POS login page
- **WHEN** the user inputs username `admin` and seed password `DevAdmin!2026`
- **AND** clicks the "INGRESAR" button
- **THEN** IF the "Actualizar Contraseña" modal is shown, the test MUST submit a valid new password `Admin123*`
- **AND** the application MUST navigate to the POS interface (`.pos-page`).

---

## Requirement 2: Complete Sales Flow at POS
The test suite MUST verify product selection, quantity adjustment, customer assignment, and checkout.

### Scenario 2.1: Add Product, Adjust Quantity and Open Checkout
- **GIVEN** an authenticated user on the POS page
- **WHEN** the user searches for a product or selects an available catalog item
- **THEN** the product MUST appear in the cart
- **WHEN** the user modifies the item quantity
- **THEN** the subtotal and total amounts MUST reflect the updated quantity
- **WHEN** the user clicks "COBRAR"
- **THEN** the Checkout modal MUST open.

---

## Requirement 3: Cuentas Abiertas (On-Hold Orders Lifecycle)
The test suite MUST verify the full lifecycle of on-hold orders: creation, editing, liquidation, and cancellation.

### Scenario 3.1: Place Sale on Hold
- **GIVEN** a POS cart with at least one item
- **WHEN** the user triggers the hold action (F4 or "EN ESPERA")
- **THEN** the hold confirmation/modal MUST be processed and the cart MUST be cleared.

### Scenario 3.2: Inspect and Edit On-Hold Order
- **GIVEN** an active on-hold order in `/pending`
- **WHEN** the user navigates to `/pending` and clicks "Editar"
- **THEN** the `EditSaleModal` MUST open with the order items
- **WHEN** the user updates quantities and saves
- **THEN** the order details in the list MUST update accordingly.

### Scenario 3.3: Checkout On-Hold Order
- **GIVEN** an active on-hold order in `/pending`
- **WHEN** the user clicks "Cobrar" on the order row
- **THEN** the `CheckoutModal` MUST open
- **WHEN** payment is confirmed
- **THEN** the order MUST be completed and removed from the active on-hold list.

### Scenario 3.4: Cancel/Delete Unpaid On-Hold Order
- **GIVEN** an active on-hold order with zero payments in `/pending`
- **WHEN** the user selects the order and clicks "Anular Pedido"
- **AND** confirms in the modal
- **THEN** the order MUST be cancelled and removed from the list.

---

## Requirement 4: Retiros Pendientes (Pending Pickups)
The test suite MUST verify pending merchandise pickup management.

### Scenario 4.1: View and Confirm Merchandise Pickup
- **GIVEN** an authenticated user on `/pickups`
- **WHEN** the user inspects pending pickups
- **AND** clicks "Confirmar Entrega" / "Marcar Retirado" for an order
- **THEN** the confirmation dialog MUST appear
- **WHEN** the action is confirmed
- **THEN** the pickup MUST be marked as delivered and a success notification MUST be displayed.
