# Delta for supplier-invoice-ocr-review

## MODIFIED Requirements

### Requirement: Document Capture

The WPF review flow MUST allow attaching a file (`png`, `jpg`, `jpeg`, `webp`, `bmp`, `tif`, `tiff` or `pdf`) through "Escanear factura (OCR)". Camera capture was removed by the maintainer on 2026-10-04 (tracker L14): file attachment is the only capture path. The existing tabular file path (xlsx/csv/xml) MUST remain unchanged.

#### Scenario: Attach an image or PDF

- GIVEN the supplier invoice view with a selected supplier
- WHEN the user picks "Escanear factura (OCR)" and selects a supported image or PDF
- THEN the file is uploaded to the extraction endpoint
- AND the returned rows and the detected invoice number populate the review state

#### Scenario: Additional image formats

- GIVEN a WEBP, BMP or TIFF invoice image
- WHEN the user selects it
- THEN the upload is accepted by the endpoint and the review state is populated

### Requirement: Low-Confidence Verification Before Approval

Low-confidence fields MUST remain visually highlighted in the editable grid (yellow 60–85, red below 60) so the reviewer checks them against the source document; the highlight MUST reflect the persisted per-field confidence and MUST clear when the reviewer edits the value (the edited value is reviewer-verified).

#### Scenario: Highlight follows persisted confidence

- GIVEN an OCR-staged line with per-field confidences
- WHEN the grid renders
- THEN each low-confidence cell shows its yellow/red highlight

#### Scenario: Editing clears the highlight

- GIVEN a highlighted cell
- WHEN the reviewer edits that field
- THEN the highlight for that field clears
- AND the value flows to confirm as usual

## REMOVED Requirements

### Requirement: Side-by-Side Staging Review

Removed by maintainer decision (ANEXO 8.160): backend page previews, zoom and page navigation are retired; replaced by the invoice number indicator next to the "Escanear factura (OCR)" button.

## ADDED Requirements

### Requirement: OCR Invoice Number Indicator

When the staged invoice originates from OCR, the staging view MUST show the best-effort detected invoice number next to the "Escanear factura (OCR)" button (with a `No detectado` placeholder when absent), and the review grid MUST keep the remaining editable columns (approval, suggested prices; no margin inputs, no supplier-code column). Without OCR origin no indicator is shown and the single-grid layout is kept.

#### Scenario: Invoice number indicator for OCR origin

- GIVEN a staged invoice extracted from OCR
- WHEN the staging view renders
- THEN the indicator next to the scan button shows the detected invoice number, or the not-detected placeholder
- AND the editable grid keeps its columns without margin inputs or the supplier-code column

#### Scenario: Non-OCR invoice unchanged

- GIVEN a staged invoice created from a tabular file
- WHEN the view renders
- THEN no invoice-number indicator is shown (single-grid layout)
