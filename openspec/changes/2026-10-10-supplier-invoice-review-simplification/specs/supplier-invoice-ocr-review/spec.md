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

Removed by maintainer decision (ANEXO 8.160): backend page previews, zoom and page navigation are retired; replaced by the OCR Context Panel.

## ADDED Requirements

### Requirement: OCR Context Panel

When the staged invoice originates from OCR, the staging view MUST show a compact left context panel with the best-effort detected invoice number (`No detectado` placeholder when absent), and the right pane keeping the editable grid (approval, suggested prices, no margin inputs). Without OCR origin the view MUST keep its current single-grid layout.

#### Scenario: Context panel for OCR origin

- GIVEN a staged invoice extracted from OCR
- WHEN the staging view renders
- THEN the left panel shows the detected invoice number, or the not-detected placeholder
- AND the editable grid stays on the right

#### Scenario: Non-OCR invoice unchanged

- GIVEN a staged invoice created from a tabular file
- WHEN the view renders
- THEN the single-grid layout is used (no context panel)
