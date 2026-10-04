# Delta for supplier-invoice-staging

## ADDED Requirements

### Requirement: Supplier Identification

The system MUST resolve each ingested invoice to a persisted `Supplier` by RIF/NIT or commercial name, and MUST block staging until an ambiguous or unmatched supplier is selected by the user.

#### Scenario: Resolve supplier by fiscal identity

- GIVEN an invoice declaring a RIF/NIT matching an existing `Supplier`
- WHEN the user starts ingestion
- THEN the invoice is bound to that `Supplier`
- AND its column-mapping template is loaded

#### Scenario: Unmatched supplier blocks staging

- GIVEN an invoice whose RIF/NIT and commercial name match no `Supplier`
- WHEN the user attempts to stage it
- THEN staging MUST halt with a supplier prompt
- AND no `SupplierInvoice` draft row is persisted until a supplier is chosen

### Requirement: Persisted Column-Mapping Template

On the first import for a supplier, the system MUST persist the confirmed column→field mapping as a `SupplierColumnMapping` template and MUST reuse it automatically on later imports for that supplier.

#### Scenario: Save mapping on first import

- GIVEN a first-time invoice for a supplier
- WHEN the user confirms the column mapping
- THEN a `SupplierColumnMapping` row is persisted for that supplier

#### Scenario: Reuse saved mapping

- GIVEN a supplier with a persisted `SupplierColumnMapping`
- WHEN the user ingests a new invoice for that supplier
- THEN the saved mapping is applied automatically
- AND the user MAY override it before staging

### Requirement: Multi-Format Ingestion

The system SHALL parse `.xlsx` (ClosedXML), `.csv`, and `.xml` (`System.Xml`) files and store each row as a draft `SupplierInvoiceLine` under a draft `SupplierInvoice`, without mutating any `Product`.

#### Scenario: Ingest each supported format

- GIVEN a `.xlsx`, `.csv`, or `.xml` supplier invoice
- WHEN the user stages it
- THEN a draft `SupplierInvoice` with its `SupplierInvoiceLine` rows is persisted
- AND no `Product` cost, margin, or stock value changes

#### Scenario: Unparseable or empty file

- GIVEN a file with no readable line rows
- WHEN the user stages it
- THEN the system MUST reject it with a `ProblemDetails` error
- AND MUST NOT persist a partial `SupplierInvoice`

### Requirement: Staging Review Semantics

Each staged line MUST show one status — `[NEW]`, `[UPDATE]` (old → new cost), `[UNCHANGED]`, or `[CONFLICT]` — and MUST prefill a per-row margin override with the product's historical margin, with the approval toggle ON by default; the suggested sale price MUST recalculate instantly in the client when the margin changes, with no backend round-trip per keystroke and no cost/margin/stock apply before confirm.

#### Scenario: Status classification per line

- GIVEN a matched line whose new cost differs from the product's current `CostPriceUSD`
- WHEN staging renders the line
- THEN it is flagged `[UPDATE]` showing old → new cost
- AND an unchanged cost is `[UNCHANGED]`, an unmatched line is `[CONFLICT]`

#### Scenario: Instant client-side recalc

- GIVEN a staged line with a pre-filled margin override
- WHEN the user edits the margin value
- THEN the suggested sale price updates instantly using `PricingCalculator.RoundPriceUp` over cost + margin
- AND no backend request is issued per keystroke

#### Scenario: No apply before confirm

- GIVEN staged and approved lines exist
- WHEN the user has not triggered confirm
- THEN `Product.CostPriceUSD`, margins, and `StockQuantity` remain unchanged
