# Specification: Shared Web & WPF Business Flows

## Flow 1: Authentication & Session Lifecycle
- **Scenario 1.1: Invalid Credentials**
  - Given the application is at the login view
  - When the user enters invalid credentials
  - Then an error message is prominently displayed without crashing.
- **Scenario 1.2: Valid Login**
  - Given the application is at the login view
  - When valid administrative or cashier credentials are submitted
  - Then the session is initialized and the main POS view is loaded.

## Flow 2: Complete POS Sales Flow
- **Scenario 2.1: Product Search & Cart Addition**
  - Given the user is on the POS screen
  - When a product is searched by SKU or name and selected
  - Then the item appears in the cart with correct unit prices (USD and Bs.S) and subtotal.
- **Scenario 2.2: Customer Assignment**
  - Given an active sale in progress
  - When the customer selection dialog/modal is triggered
  - Then a registered customer can be selected and associated with the transaction.
- **Scenario 2.3: Checkout Modal Trigger**
  - Given items present in the cart
  - When the Checkout (Cobrar) action is triggered
  - Then the checkout dialog/modal is displayed with payment method options.

## Flow 3: Cuentas Abiertas (On-Hold Orders Lifecycle)
- **Scenario 3.1: Hold Sale from POS**
  - Given a cart with items and a registered customer assigned
  - When the Hold Sale (Guardar en Espera) action is executed
  - Then the order is placed on hold and the active cart resets.
- **Scenario 3.2: View & Filter Pending Orders**
  - Given existing orders on hold
  - When navigating to Cuentas Abiertas
  - Then orders are listed with customer name, total amounts, and status.
- **Scenario 3.3: Actions on Pending Orders**
  - Given a pending order selected in the list
  - Then action controls for Cobrar (Liquidar), Editar, and Liberar/Cancelar are accessible.

## Flow 4: Retiros Pendientes (Pending Pickups)
- **Scenario 4.1: View & Filter Pickups**
  - Given orders with physical delivery pending
  - When navigating to Retiros Pendientes
  - Then pickup records are displayed with invoice number, customer, and items.
- **Scenario 4.2: Confirm Delivery**
  - Given a pending pickup item
  - When the delivery confirmation is triggered
  - Then the status reflects completed delivery.

## Flow 5: Cash Drawer (Caja Registradora)
- **Scenario 5.1: Session Status & Balance**
  - Given the Cash Drawer view
  - Then the current active session status, opening balance, and transaction history are visible.
- **Scenario 5.2: Cash Movement Controls**
  - Given the Cash Drawer view
  - Then controls for Cash In (Entrada) and Cash Out (Salida) are accessible.

## Flow 6: Product Catalog & Inventory Lookup
- **Scenario 6.1: Catalog Search & Stock Display**
  - Given the Catalog / Inventory view
  - When searching for a product
  - Then matching products are listed with price, profit margin, and current stock.
