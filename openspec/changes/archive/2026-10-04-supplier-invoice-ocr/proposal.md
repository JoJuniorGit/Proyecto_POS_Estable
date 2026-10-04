# Proposal: Supplier Invoice OCR Digitization & Auto-Loading

## Intent

Supplier invoices arrive as photos, scans or PDFs. Manual typing into staging is slow and error-prone. Add an open-source OCR pipeline (Tesseract + image preprocessing) running in the backend that extracts structured rows (codes, description, quantity, unit cost) plus a supplier hint, and a WPF side-by-side review (image with zoom + editable grid with per-field confidence highlighting) so the reviewer verifies against the source image before approving.

## Scope

### In Scope
- Backend OCR pipeline: image/PDF decode, preprocessing (grayscale, contrast, binarization, deskew), Tesseract 5 extraction behind `IOcrEngine`, and heuristic table parsing driven by the supplier column-mapping template (with generic keyword fallback).
- Per-field confidence scoring (name/quantity/unit cost) returned by extraction and persisted on staged lines.
- `POST /api/supplier-invoices/ocr-extract` multipart endpoint (png/jpg/jpeg/pdf, ≤20 MB, ≤5 pages, Admin/Manager, ProblemDetails) returning rows, page previews (PNG) and best-effort supplier/RIF hints.
- OCR-sourced staging: stage without column mapping (`OcrSourced` flag), confidence persisted on `SupplierInvoiceLine`.
- WPF: "Escanear factura (OCR)" file path (png/jpg/jpeg/webp/bmp/tif/tiff/pdf; camera capture removed by maintainer decision 2026-10-04, L14), side-by-side staging (backend-rendered previews with zoom/page navigation + existing editable grid), yellow/red low-confidence cell highlighting and reviewer corrections flowing to confirm.

### Out of Scope
- Web.Frontend; OCR of tabular formats (xlsx/csv/xml keep the current path); automatic invoice approval (human-in-the-loop gated by confirm); handwriting recognition; multi-branch concerns.

## Capabilities

### New Capabilities
- `supplier-invoice-ocr-extraction`: OCR engine + preprocessing + template-aware parsing + confidence scoring + extraction endpoint.
- `supplier-invoice-ocr-review`: WPF capture (file/camera) and side-by-side low-confidence review.

### Modified Capabilities
- `supplier-invoice-staging`: OCR-sourced staging without template + per-field confidence persisted and rendered.

## Approach

New backend partials/services inside `Inventory.Module` (engine wrapper `IOcrEngine` → `TesseractOcrEngine`; pure preprocessing functions over OpenCvSharp; `PDFtoImage` raster; a deterministic parser over word boxes). Endpoint in `SupplierInvoicesController` (multipart, Admin/Manager, size/page caps). Schema: three nullable `numeric(5,2)` confidence columns on `SupplierInvoiceLines` (additive migration) + `StageSupplierInvoiceRequestDto.OcrSourced`. Client: `Desktop.Client.Core` gains an OCR client service + VM state (previews, zoom, camera), and `Desktop.Client` gains a camera dialog (OpenCvSharp `VideoCapture`) and the split-view XAML reusing the existing grid and `ViewModelProxy`.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `Directory.Packages.props`, `Inventory.Module.csproj`, `Backend.API.csproj` | Modified | Tesseract, OpenCvSharp4 (+win runtime), PDFtoImage, tessdata |
| `Core/DTOs/SupplierInvoiceDtos.cs`, `Core/Common/OcrConfidence.cs` | Modified/New | Extraction DTOs, confidence bands |
| `Core/Entities/SupplierInvoiceLine.cs`, `InventoryDbContext`, migration | Modified | 3 confidence columns (nullable) |
| `Inventory.Module/Services/Ocr/*`, `SupplierInvoiceService.*` | New/Modified | Engine, preprocessing, parser, extraction service, OCR staging flag |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | Modified | `ocr-extract` endpoint |
| `Desktop.Client.Core/Services/SupplierInvoiceService*.cs`, VMs | Modified | Upload + OCR flow state |
| `Desktop.Client/Views/*`, `CameraCaptureDialog` | New/Modified | Split view, zoom, highlighting, camera |
| CI (`ci.yml`) | Modified | Gated native OCR smoke flag (suite already runs on windows-2025) |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| OCR quality on real photos/scan variance | High | Preprocessing pipeline; human-in-the-loop review is mandatory before confirm; per-field confidence highlighting |
| Native dependency packaging (Tesseract/OpenCV/PDFium/tessdata) | Med | NuGet runtime packages on Windows CI; data-pack or vendored tessdata with documented copy; engine behind interface so the suite runs without natives |
| Heuristic parser false positives (wrong columns/rows) | Med | Template keyword columns first, generic fallback second; numeric regex validation; editable grid with highlighting; confirm remains the single apply point |
| Feature exceeds review budget | High | Chained work units: engine core → parser → endpoint → schema/staging → client → UI |
| Client size growth (OpenCvSharp for camera) | Med | Camera-only usage isolated in `Desktop.Client`; documented trade-off vs TFM bump alternative |

## Rollback Plan

- Additive migration only (3 nullable columns); down drops them.
- New endpoint + engine services are inert until the UI uses them; revert nav/VM wiring and the endpoint to disable. Existing tabular flow untouched.

## Dependencies

- NuGet: `Tesseract` (5.x wrapper), `OpenCvSharp4` + runtime packs, `PDFtoImage`; tessdata spa+eng (pack or vendored). All Apache-2.0/MIT family; CI vulnerability gate must stay clean.

## Success Criteria

- [ ] Backend extracts rows with per-field confidence from png/jpg/pdf uploads; unsupported types/sizes rejected with ProblemDetails.
- [ ] Parser deterministically resolves template-keyword and generic columns from word boxes (unit tests, no native engine required).
- [ ] OCR-sourced staging works without a column mapping and persists per-field confidence; confirm remains the only apply point.
- [ ] WPF split review shows backend previews with zoom and highlights yellow/red low-confidence cells; camera capture degrades gracefully.
- [ ] Build 0/0; full suite green; coverage gates maintained; gated native smoke documented.
