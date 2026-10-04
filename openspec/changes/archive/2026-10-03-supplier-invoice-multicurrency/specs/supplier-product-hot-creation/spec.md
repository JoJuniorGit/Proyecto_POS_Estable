# Delta for supplier-product-hot-creation

## ADDED Requirements

### Requirement: Create Product From a Staged Line

The system MUST expose an Admin/Manager atomic operation that creates a catalog product from a staged draft line that has no resolved product, resolving the line to the new product. The product identity is the operator-provided name and the captured universal barcode; the operation MUST create the product with zero cost, zero margins, zero prices and zero stock (confirm remains the single apply point for cost/margin/price/stock). The operation MUST be rejected when the invoice is not a draft or the line already resolves to a product. The response MUST return the refreshed invoice detail.

#### Scenario: Create product from line

- GIVEN a draft line with no resolved product
- WHEN an Admin submits a valid barcode and name
- THEN a `Product` is created with `SKU = barcode`, zero cost and zero stock
- AND the line resolves to the new product and is reclassified (`[UPDATE] 0.00 → cost`)
- AND the response contains the refreshed invoice detail

#### Scenario: Already resolved line cannot be created again

- GIVEN a line already resolved to a product
- WHEN creation is requested
- THEN the system MUST reject it with a `ProblemDetails` error
- AND MUST NOT persist any product or alias change

#### Scenario: Cashier is blocked

- GIVEN an authenticated user without catalog mutation rights (Cashier)
- WHEN creation is requested
- THEN the system MUST respond 403 (RBAC) and persist nothing

### Requirement: Mandatory Universal Barcode Capture

Product creation from a line MUST require the operator to capture the physical product's universal barcode (EAN/UPC, 8–14 digits); the system MUST NOT inherit the supplier's invoice codes (the line's supplier code or file barcode column) as the product barcode/SKU.

#### Scenario: Missing or malformed barcode is rejected

- GIVEN a creation request with an empty barcode, or one that is not 8–14 digits
- WHEN submitted
- THEN the system MUST reject it with a `ProblemDetails` error
- AND MUST NOT persist a product, alias or line change

#### Scenario: No inheritance of invoice codes

- GIVEN a staged line whose supplier code is `ABC-123`
- WHEN the creation dialog opens
- THEN the barcode field starts empty
- AND `ABC-123` is never used as the product `SKU`

#### Scenario: Captured barcode becomes the SKU

- GIVEN a captured EAN `7591234567890`
- WHEN the product is created
- THEN `Product.SKU` equals `7591234567890`

### Requirement: Supplier Code Alias Persistence

The supplier's internal code MUST be persisted in the `SupplierProductCode` cross-reference so future invoices from that supplier auto-match. The alias MUST be upserted when a product is created from a line carrying a supplier code, and on confirm for every applied line carrying a supplier code (see `supplier-invoice-apply`).

#### Scenario: Alias written on creation

- GIVEN a line with supplier code `ABC-123` and a successful creation
- THEN `SupplierProductCode(SupplierId, "ABC-123")` maps to the new product

#### Scenario: Auto-recognition on the next invoice

- GIVEN a persisted alias `ABC-123 → P`
- WHEN a later invoice from the same supplier includes `ABC-123`
- THEN the line resolves to `P` via the supplier-code match method
