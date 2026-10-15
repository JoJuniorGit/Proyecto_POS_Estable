# Delta for supplier-invoice-ocr-extraction

## MODIFIED Requirements

### Requirement: OCR Extraction Endpoint

The system MUST expose `POST /api/supplier-invoices/ocr-extract` (Admin/Manager) accepting a multipart upload of `png`, `jpg`, `jpeg`, `webp`, `bmp`, `tif`, `tiff` or `pdf` (max 20 MB, max 5 PDF pages) plus the optional selected supplier id and mapping names; it MUST respond with the extracted rows (with confidences) and best-effort detected RIF/supplier-name/invoice-number strings — and MUST NOT return page previews (retired by the maintainer, ANEXO 8.160) — and MUST reject unsupported types/sizes with `ProblemDetails` without persisting anything.

#### Scenario: Extract from an image upload

- GIVEN an authenticated Admin uploading a JPG invoice
- WHEN the endpoint processes it
- THEN the response contains extracted rows with per-field confidence
- AND no page previews are returned

#### Scenario: PDF multi-page extraction

- GIVEN a PDF with up to 5 pages
- WHEN the endpoint processes it
- THEN rows from every page are returned in page order

#### Scenario: Unsupported input rejected

- GIVEN a `.docx` upload or a file above 20 MB
- WHEN the endpoint processes it
- THEN it MUST respond with a `ProblemDetails` error
- AND MUST NOT persist anything

#### Scenario: Cashier blocked

- GIVEN an authenticated Cashier
- WHEN the endpoint is called
- THEN the system MUST respond 403 (RBAC)

#### Scenario: Best-effort detected identifiers

- GIVEN an invoice image containing a RIF, a supplier name and a text like `FACTURA: *0238317`
- WHEN extraction completes
- THEN the response MAY include the detected RIF/supplier name/invoice number
- AND nothing is auto-selected or persisted on the strength of those hints
