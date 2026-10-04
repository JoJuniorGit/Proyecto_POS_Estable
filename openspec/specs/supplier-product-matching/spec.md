# supplier-product-matching Specification

## Purpose

Deterministic invoice-line to product matching: barcode (SKU) first, supplier-code alias second, pg_trgm fuzzy name third, with a configurable similarity threshold and stable tie-breaks.

## Requirements

### Requirement: Matching Priority

For each invoice line the system MUST resolve a product using priority barcode (`SKU`) > supplier code > fuzzy name via `pg_trgm` similarity, and MUST NOT fall back to product creation.

#### Scenario: Barcode wins

- GIVEN a line whose barcode matches exactly one `Product.SKU`
- WHEN matching runs
- THEN that product is selected regardless of name similarity

#### Scenario: Supplier code fallback

- GIVEN a line with no matching barcode but a supplier code linked to a product
- WHEN matching runs
- THEN the linked product is selected

#### Scenario: Fuzzy name fallback

- GIVEN a line with no matching barcode or supplier code
- WHEN `pg_trgm` similarity to a product name is at or above the configured threshold
- THEN the highest-similarity product is selected

### Requirement: Configurable Similarity Threshold

The fuzzy-name similarity threshold MUST be configurable and read from system settings at match time.

#### Scenario: Threshold boundary

- GIVEN a configured similarity threshold T
- WHEN the best name similarity is below T
- THEN the line is left unmatched
- AND MUST NOT be force-matched

### Requirement: Deterministic Tie-Breaks

When multiple products qualify for a line, the system MUST break ties deterministically: exact code match first, then highest similarity score, then lowest product Id.

#### Scenario: Two equal-similarity candidates

- GIVEN two products with equal similarity at or above the threshold
- WHEN the tie is resolved
- THEN the product with the lower product Id is selected
- AND the outcome is identical across repeated runs

### Requirement: Unmatched Lines Are Informational

A line with no product match MUST become `[CONFLICT]`/informational and MUST NOT create a product.

#### Scenario: Match-only v1

- GIVEN a line that cannot be matched
- WHEN staging completes
- THEN the line is `[CONFLICT]` and marked informational
- AND no `Product` row is created
