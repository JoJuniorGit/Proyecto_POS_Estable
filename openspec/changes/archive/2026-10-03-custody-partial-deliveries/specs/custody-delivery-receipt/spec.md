# Delta for custody-delivery-receipt

## ADDED Requirements

### Requirement: Delivery Note Generation

The system MUST generate an on-demand PDF delivery note (`Nota de Despacho`) for any recorded delivery event, built exclusively from immutable log data: event date/time (rendered in Venezuelan local time), delivering cashier name, sale id and invoice number, customer name/cedula when present, and each delivered item with product name and quantity. It MAY include unit prices and subtotals from the sale-item snapshots. It MUST clearly present itself as a delivery note for custody goods, not as a fiscal invoice, and MUST NOT recalculate any monetary value.

#### Scenario: Note content for a partial event
- GIVEN a recorded event delivering 4 of 10 units
- WHEN the note is generated
- THEN it shows the event date/time, cashier, customer, the item with quantity 4, and the remaining pending summary
- AND no monetary snapshot is modified

#### Scenario: Reprint later returns the same event
- GIVEN an event recorded days ago
- WHEN its note is retrieved again
- THEN the content reflects the stored log data, not current sale state

### Requirement: Retrieval Endpoint and Access

The delivery note MUST be retrievable as `application/pdf` from a sale-scoped endpoint that follows the pickup authorization rules (authenticated, sale-scoped, Driver blocked). An unknown delivery id or a delivery not belonging to the sale MUST return `404`; unauthorized access MUST return `403`; errors MUST use ProblemDetails.

#### Scenario: Unknown delivery id
- GIVEN a sale
- WHEN the note of a non-existent delivery id is requested
- THEN the response is `404` ProblemDetails

#### Scenario: Delivery of another sale
- GIVEN delivery D of sale A
- WHEN the note is requested through sale B
- THEN the response is `404` and no PDF is generated
