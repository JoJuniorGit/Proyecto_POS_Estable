# Delta for supplier-invoice-ocr-extraction

## ADDED Requirements

### Requirement: OCR Engine and Image Preprocessing

The system MUST integrate an open-source OCR engine (Tesseract 5.x) behind an engine abstraction, and MUST preprocess every incoming page before OCR: grayscale conversion, contrast increase, binarization and skew correction (deskew). Engine- and preprocessing-dependent behavior MUST be unit-testable without the native engine (canned word-box data), with one environment-gated smoke test exercising the real engine.

#### Scenario: Preprocessing pipeline steps

- GIVEN a photographed invoice image
- WHEN it enters the OCR pipeline
- THEN the image passes grayscale, contrast, binarization and deskew steps in order
- AND the pipeline is covered by deterministic tests using synthetic images

#### Scenario: Engine behind abstraction

- GIVEN unit tests without the native Tesseract libraries
- WHEN extraction logic is exercised with canned word-box data
- THEN no native engine call is required
- AND a gated smoke test exercises the real engine when enabled

### Requirement: Template-Aware Column Extraction

The backend MUST parse the OCR word boxes into invoice rows using (a) the selected supplier's column-mapping template names as header keywords when available, falling back to (b) generic keywords (description/name/product; quantity/cant; price/cost/unit/amount; code/SKU/ref; barcode), and MUST group words into rows by vertical position and into columns by horizontal alignment to the detected header anchors. Numeric fields MUST be parsed tolerantly (comma/dot decimal separators).

#### Scenario: Template keyword columns win

- GIVEN an OCR page whose header contains the supplier template names for description, quantity and unit cost
- WHEN parsing runs
- THEN the columns are resolved from those headers
- AND rows assign words to the matched columns by alignment

#### Scenario: Generic fallback

- GIVEN a page without template-matching headers
- WHEN parsing runs
- THEN generic keyword detection resolves the columns
- AND unaffected words are ignored or attached to the description column

#### Scenario: Tolerant numeric parsing

- GIVEN quantity/cost tokens like `1.234,56`, `1,234.56`, `12,5` or `$ 4.250`
- WHEN the parser reads them
- THEN it produces the expected decimal values

### Requirement: Confidence Scoring

Every extracted field (name, quantity, unit cost) MUST carry a confidence score derived from its contributing words' OCR confidences, expressed as a percentage 0–100 with one-decimal precision; unspecified fields MUST carry a zero-confidence score.

#### Scenario: Field confidence aggregation

- GIVEN a row whose quantity came from a single high-confidence token and whose name spans several low-confidence tokens
- WHEN extraction completes
- THEN each field exposes its own aggregated confidence
- AND a field that could not be resolved reports zero

### Requirement: OCR Extraction Endpoint

The system MUST expose `POST /api/supplier-invoices/ocr-extract` (Admin/Manager) accepting a multipart upload of `png`, `jpg`, `jpeg`, `webp`, `bmp`, `tif`, `tiff` or `pdf` (max 20 MB, max 5 PDF pages) plus the optional selected supplier id and mapping names; it MUST respond with the extracted rows (with confidences), one PNG preview per processed page, and best-effort detected RIF/supplier-name strings, and MUST reject unsupported types/sizes with `ProblemDetails` without persisting anything.

#### Scenario: Extract from an image upload

- GIVEN an authenticated Admin uploading a JPG invoice
- WHEN the endpoint processes it
- THEN the response contains extracted rows with per-field confidence
- AND at least one PNG preview of the processed page

#### Scenario: PDF multi-page extraction

- GIVEN a PDF with up to 5 pages
- WHEN the endpoint processes it
- THEN rows from every page are returned in page order
- AND one preview per page is returned

#### Scenario: Unsupported input rejected

- GIVEN a `.docx` upload or a file above 20 MB
- WHEN the endpoint processes it
- THEN it MUST respond with a `ProblemDetails` error
- AND MUST NOT persist anything

#### Scenario: Cashier blocked

- GIVEN an authenticated Cashier
- WHEN the endpoint is called
- THEN the system MUST respond 403 (RBAC)

#### Scenario: Supplier hint is best-effort only

- GIVEN an invoice image containing a RIF
- WHEN extraction completes
- THEN the response MAY include the detected RIF/supplier name
- AND nothing is auto-selected or persisted on the strength of that hint
