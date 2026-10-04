# Delta for supplier-invoice-ocr-review

## ADDED Requirements

### Requirement: Document Capture

The WPF review flow MUST allow attaching a file (`png`, `jpg`, `jpeg`, `pdf`) or capturing a photo with the device camera; the camera path MUST degrade gracefully (clear message, no crash) when no camera is available. The existing tabular file path (xlsx/csv/xml) MUST remain unchanged.

#### Scenario: Attach an image or PDF

- GIVEN the supplier invoice view with a selected supplier
- WHEN the user picks "Escanear factura (OCR)" and selects a JPG/PDF
- THEN the file is uploaded to the extraction endpoint
- AND the returned rows and previews populate the review state

#### Scenario: Camera capture

- GIVEN a device with an available camera
- WHEN the user opens camera capture and takes a photo
- THEN the captured image flows into the same extraction path

#### Scenario: No camera available

- GIVEN a device without a usable camera
- WHEN the user opens camera capture
- THEN a clear message is shown
- AND the application keeps working with the file path only

### Requirement: Side-by-Side Staging Review

When the staged invoice originates from OCR, the staging view MUST split into a left pane showing the backend-rendered page previews with zoom and page navigation, and the right pane keeping the existing editable grid (margins, approval, suggested prices). Without OCR origin the view MUST keep its current single-grid layout.

#### Scenario: Split layout for OCR origin

- GIVEN a staged invoice extracted from OCR
- WHEN the staging view renders
- THEN the source previews appear on the left with zoom and page navigation
- AND the editable grid stays on the right

#### Scenario: Zoom

- GIVEN the left preview pane
- WHEN the user zooms in/out (wheel or buttons)
- THEN the preview scales within the pane
- AND page navigation switches between preview pages

#### Scenario: Non-OCR invoice unchanged

- GIVEN a staged invoice created from a tabular file
- WHEN the view renders
- THEN the single-grid layout is used (no preview pane)

### Requirement: Low-Confidence Verification Before Approval

Low-confidence fields MUST be visually highlighted in the editable grid (yellow 60–85, red below 60) so the reviewer checks them against the source image; the highlight MUST reflect the persisted per-field confidence and MUST clear when the reviewer edits the value (the edited value is reviewer-verified).

#### Scenario: Highlight follows persisted confidence

- GIVEN an OCR-staged line with per-field confidences
- WHEN the grid renders
- THEN each low-confidence cell shows its yellow/red highlight

#### Scenario: Editing clears the highlight

- GIVEN a highlighted cell
- WHEN the reviewer edits that field
- THEN the highlight for that field clears
- AND the value flows to confirm as usual
