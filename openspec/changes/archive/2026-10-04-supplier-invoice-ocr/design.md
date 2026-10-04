# Design: Supplier Invoice OCR Digitization & Auto-Loading

## Technical Approach

Backend-owned OCR pipeline: uploads are decoded (PDF → page bitmaps via PDFium), preprocessed with OpenCvSharp (grayscale → CLAHE contrast → Otsu binarization → deskew), recognized by Tesseract 5 (spa+eng) behind `IOcrEngine`, and parsed deterministically from word boxes (TSV) into invoice rows: row clustering by vertical position, column resolution from the supplier's mapping-template keywords (generic fallback), tolerant numeric parsing, and conservative per-field confidence (minimum of contributing word confidences, 0–100). The endpoint returns rows + PNG previews + best-effort supplier/RIF hints; the WPF client stages the rows as OCR-sourced (no template), persists per-field confidence on the lines, and renders the side-by-side review (previews with zoom on the left, the existing editable grid on the right) with yellow/red low-confidence highlighting. Confirm remains the single apply point; classification, margins and pricing are untouched.

## Architecture Decisions

| # | Decision | Choice | Alternatives | Rationale |
|---|----------|--------|--------------|-----------|
| D1 | OCR engine | **Tesseract 5.x** (`Tesseract` NuGet) behind `IOcrEngine` | Windows.Media.Ocr (not open source, Windows-only); PaddleOCR (Python runtime) | User's named example; leading open-source engine; Apache-2.0; abstraction keeps the suite deterministic without natives. |
| D2 | Pipeline location | **Backend** (engine + preprocessing + parsing) | Client-side OCR | User criteria ("Procesamiento y Backend"); native deps centralized; client stays thin; CI already runs `net10.0-windows` on windows-2025. |
| D3 | Preprocessing | **OpenCvSharp4**: grayscale → CLAHE → Otsu (adaptive fallback) → deskew (Hough/minAreaRect) | Magick.NET (-deskew built-in, less control) | Fine-grained control per step; each step a pure function testable with synthetic images. |
| D4 | PDF handling | **PDFtoImage** (PDFium, MIT) backend; **previews rendered by the backend** | Client-side PDF rendering (adds PDF stack to client) | Uniform previews for images and PDFs ("what the OCR saw"); client needs no PDF dependency. |
| D5 | Camera | **OpenCvSharp4 `VideoCapture`** in `Desktop.Client` | WinRT MediaCapture (requires TFM bump → cascades to tests), skip camera | Avoids the `net10.0-windows10.0.x` cascade; camera-only usage isolated; graceful degradation when no camera; ~45 MB native trade-off documented. **Updated 2026-10-04 (T10): REMOVED by maintainer decision (tracker L14) — capture is file-only; client OpenCvSharp/WpfExtensions packages and the System.Drawing.Common bump were reverted.** |
| D6 | Parser input | **Tesseract TSV word boxes**; rows by Y-clustering (median-height tolerance), columns by X-alignment to header anchors | Plain text + regex only | Positional data resolves multi-column invoices reliably; boxes make it deterministic and unit-testable with canned data. |
| D7 | Column keywords | Supplier `SupplierColumnMapping` names first (sent with the request), generic fallback second | Generic only | Satisfies "reglas heurísticas basadas en la Planilla de Mapeo del Proveedor"; works for new suppliers via fallback. |
| D8 | Confidence aggregation | **Minimum** of contributing words' confidences (conservative), 0–100 with 1 decimal; unresolved field = 0 | Average | Stricter flagging means the reviewer verifies more, never less; deterministic and simple to test. |
| D9 | Confidence bands | `Core.Common.OcrConfidence`: red < 60, yellow 60–85, none ≥ 85 | Configurable via settings now | User asked for yellow/red; constants shared by client/server; can move to `SystemSettings` later without schema impact. |
| D10 | Confidence persistence | 3 nullable `numeric(5,2)` columns on `SupplierInvoiceLines` (`OcrNameConfidence`, `OcrQuantityConfidence`, `OcrUnitCostConfidence`) | Client-only dictionary | The staged grid renders after staging (server round-trip); persisted values survive reloads; null for non-OCR lines. |
| D11 | OCR staging discriminator | `StageSupplierInvoiceRequestDto.OcrSourced` (default false) | Implicit (confidences present), relax mapping requirement always | First-import template requirement stays for tabular flow; OCR rows have no columns to map; explicit flag is zero-surprise. |
| D12 | Extraction endpoint | `POST /api/supplier-invoices/ocr-extract` multipart (file + optional supplierId + flat mapping fields) → rows + previews + hints | JSON with base64 file | Multipart is the natural upload shape; flat form fields avoid nested multipart binding; RBAC inherited from the controller class. |
| D13 | Client flow | New "Escanear factura (OCR)" button (file only since T10); extraction → review state (previews/page/zoom) → OCR-sourced stage; tabular path untouched | Extend the existing file filter only | Keeps the two flows legible; no column-mapping UI for OCR. |
| D14 | tessdata delivery | Prefer data NuGet pack; else vendored `Backend.API/tessdata` (spa+eng) copied to output; engine datapath configured | Runtime download | Deterministic local install; verified in T2 (pack availability is an implementation check). |
| D15 | CI | Suite stays mock-based (`IOcrEngine` stub); one gated native smoke (`TEST_OCR_NATIVE=1`) enabled in CI; vulnerability gate must stay clean | Native tests always on | Keeps CI deterministic; natives exercised deliberately. |

**Unchanged invariants:** line status rule (`[NEW]`/`[UPDATE]`/`[UNCHANGED]`), approval gating, margin recalculation, no apply before confirm, alias learning, zero-trust re-validation, decimal-only money.

## Data Flow

```
WPF capture (file: png/jpg/jpeg/webp/bmp/tif/tiff/pdf)
  → POST /api/supplier-invoices/ocr-extract (multipart: file, supplierId?, mapping names)
  ▼
OcrExtractionService
  ├─ decode: image bytes | PDFtoImage page bitmaps (≤5 pages, ≤20 MB total)
  ├─ preprocess (OpenCvSharp): grayscale → CLAHE → Otsu → deskew
  ├─ IOcrEngine.RecognizeAsync (Tesseract TSV: word, box, confidence) per page
  ├─ InvoiceTableParser: rows (Y) + columns (mapping keywords → generic fallback) + numeric regex
  └─ confidence per field (min of contributing words) + previews (PNG per page) + RIF/name hints
  ▼
OcrExtractionResultDto → WPF review state (previews, page, zoom)
  → StageAsync(OcrSourced = true, lines with confidences)   // no column mapping
  ▼
Draft invoice lines persist confidences → grid highlights yellow/red → operator edits/verifies → confirm applies (unchanged)
```

## File Changes

| File | Action | Description |
|------|--------|-------------|
| `Directory.Packages.props`, `Inventory.Module.csproj`, `Backend.API.csproj`, `Desktop.Client.csproj` | Modify | Tesseract, OpenCvSharp4(+runtime), PDFtoImage, tessdata delivery |
| `Core/Common/OcrConfidence.cs` | Create | Bands (red < 60, yellow < 85) |
| `Core/DTOs/SupplierInvoiceDtos.cs` | Modify | `OcrExtractionResultDto`, `OcrExtractedLineDto`, `OcrSourced`, line confidences |
| `Core/Interfaces/IOcrEngine.cs`, `ISupplierInvoiceService` | Create/Modify | Engine abstraction + `ExtractOcrAsync` |
| `Inventory.Module/Services/Ocr/OcrExtractionService.cs` | Create | Decode → preprocess → engine → parser → DTO |
| `Inventory.Module/Services/Ocr/ImagePreprocessor.cs` | Create | Pure OpenCV steps |
| `Inventory.Module/Services/Ocr/InvoiceTableParser.cs` | Create | Word boxes → rows/columns/confidences |
| `Inventory.Module/Services/Ocr/TesseractOcrEngine.cs` | Create | Native engine wrapper |
| `Core/Entities/SupplierInvoiceLine.cs`, `InventoryDbContext`, migration | Modify | 3 nullable confidence columns |
| `Inventory.Module/Services/SupplierInvoiceService.Staging.cs` / `Creation`-style partial | Modify | `OcrSourced` handling + confidence persistence |
| `Backend.API/Controllers/SupplierInvoicesController.cs` | Modify | `ocr-extract` endpoint |
| `Backend.API/tessdata/*` (conditional) | Create | Vendored spa+eng if no data pack |
| `Desktop.Client.Core/Services/SupplierInvoiceService.cs`, `ISupplierInvoiceService.cs` | Modify | Upload + DTO pass-through |
| `Desktop.Client.Core/ViewModels/SupplierInvoiceViewModel*.cs` (+`OcrReview` partial) | Modify | OCR flow state, previews, zoom, editable cells + confirm corrections |
| `Desktop.Client/Views/SupplierInvoiceView.xaml` | Modify | Split view + highlights + OCR button + editable cells |
| `Desktop.Client/Views/CameraCaptureDialog.xaml(+.cs)` + `ICameraCaptureService` | Removed (T10) | Camera capture removed by maintainer decision (L14); client OpenCvSharp/WpfExtensions packages and the System.Drawing.Common bump reverted. |
| `Desktop.Client.Core/Services/IDialogService.cs`, `WpfDialogService*` | Modify | `ShowCameraCaptureDialog` component REMOVED by T10 (L14); dialog service back to its pre-camera surface |
| Tests: `Unit/Ocr*Tests`, `Unit/SupplierInvoice*Tests`, `Integration/Ocr*`, `Integration/SupplierInvoice*` | Create/Modify | Parser/preprocessing/endpoint/staging/client coverage |
| `.github/workflows/ci.yml` | Modify | `TEST_OCR_NATIVE=1` for the gated smoke |

## Interfaces / Contracts

```csharp
// Core/Interfaces/IOcrEngine.cs — engine boundary (tests use a stub)
public interface IOcrEngine
{
    Task<OcrPage> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default);
}
public sealed record OcrImage(byte[] EncodedBytes, int PixelWidth, int PixelHeight);
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height, double Confidence); // 0-100
public sealed record OcrPage(IReadOnlyList<OcrWord> Words);

// DTO boundary
public sealed record OcrExtractedLineDto(
    string? SupplierCode, string? Barcode, string? Name,
    decimal? Quantity, decimal? UnitCost,
    decimal NameConfidence, decimal QuantityConfidence, decimal UnitCostConfidence);
public sealed record OcrExtractionResultDto(
    IReadOnlyList<OcrExtractedLineDto> Lines,
    IReadOnlyList<string> PreviewPagesBase64,
    string? DetectedRif, string? DetectedSupplierName);

// staging additions (defaults keep existing callers compiling)
public sealed record StageSupplierInvoiceRequestDto(..., bool OcrSourced = false);
public sealed record StageLineDto(..., decimal? OcrNameConfidence = null,
    decimal? OcrQuantityConfidence = null, decimal? OcrUnitCostConfidence = null);

// endpoint
POST /api/supplier-invoices/ocr-extract  [Admin,Manager]  (multipart/form-data)
  fields: file (png|jpg|jpeg|webp|bmp|tif|tiff|pdf, <=20 MB), supplierId?, nameColumn?, quantityColumn?, unitCostColumn?, supplierCodeColumn?, barcodeColumn?
  → 200 OcrExtractionResultDto | 400 ProblemDetails | 403 | 413/400 size
```

## Testing Strategy

| Layer | What to Test | Approach |
|-------|-------------|----------|
| Unit | Preprocessing steps (synthetic images: deskew angle recovery, binarization output); parser (canned TSV: template keywords, generic fallback, tolerant numerics, row/column clustering, confidence aggregation, unresolved fields); confidence bands | xUnit; pure functions; no native engine |
| Unit (service) | Extraction orchestration with stub `IOcrEngine` + stub decoder (pages, caps, hints, previews) | xUnit + mocks |
| Integration | Endpoint contracts: image/PDF happy path (stub engine), unsupported type/size 400, Cashier 403 (TestServer), no persistence | `WebApplicationFactory` patterns |
| Unit (staging) | `OcrSourced` first-import without template; confidence persisted; tabular flow still requires mapping | In-memory `InventoryDbContext` |
| Client unit | OCR upload service route/payload; VM flow (extract → stage with flag/confidences → review state); zoom/page state; highlight mapping logic (bands); confirm corrections gating | xUnit + mocks (headless) |
| Gated | Real Tesseract native smoke on a generated image (`TEST_OCR_NATIVE=1`, set in CI) | xUnit with env gate + silent local skip |
| Regression | Full suite (baseline 1651) + coverage gates | `dotnet test` |

## Threat Matrix

Uploaded documents are untrusted input: the endpoint enforces extension/content allowlist, size (≤20 MB) and page caps (≤5), performs no shell/process execution, persists nothing from extraction, and stays RBAC-gated (Admin/Manager). Native libraries (Tesseract/OpenCV/PDFium) are pinned via NuGet with the CI vulnerability gate. OCR output flows only into the already-validated staging path (zero-trust), and never applies to the catalog without confirm.

## Migration / Rollout

Additive migration `AddSupplierInvoiceLineOcrConfidence`: three nullable `numeric(5,2)` columns; Down drops them. Feature is inert until the OCR button/endpoint are used; tabular flow and all existing behavior remain untouched.

## PR Slicing

1. Backend OCR core (packages, engine abstraction, preprocessing, PDF raster, tessdata, gated smoke).
2. Heuristic parser (+ deterministic tests).
3. Extraction endpoint (+ DTOs, DI, caps, RBAC; stub-engine tests).
4. Schema + OCR staging (`OcrSourced`, confidences).
5. Client service + VM flow (+ headless tests).
6. WPF UI (split view, zoom, highlights, editable OCR cells) + wiring.

Apply `size:exception` only if a slice exceeds the 400-line review budget.

## Open Questions

None. Assumptions/updates: camera was removed by maintainer decision 2026-10-04 (D5/T10, tracker L14) and image formats extended to webp/bmp/tif/tiff; tessdata delivery verified in T2 (D14); confidence bands are constants now, movable to settings later (D9).
