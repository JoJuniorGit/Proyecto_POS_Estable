# Tasks: Supplier Invoice OCR Digitization & Auto-Loading

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~2,400–3,200 |
| Budget risk | High |
| Chained PRs | Yes |
| Split | 6 chained work units |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached from the V0.15 chain) |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Goal / PR | Focused test command | Harness / Rollback boundary |
|---|---|---|
| 1. Backend OCR core (engine, preprocessing, PDF, tessdata) | `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~Ocr"` | Remove package refs + Ocr services |
| 2. Heuristic parser | `... --filter "FullyQualifiedName~OcrParser"` | Revert parser service |
| 3. Extraction endpoint | `... --filter "FullyQualifiedName~OcrExtraction\|FullyQualifiedName~SupplierInvoice"` | Revert endpoint + DTOs |
| 4. Schema + OCR staging | `... --filter "FullyQualifiedName~SupplierInvoiceStaging\|FullyQualifiedName~SupplierInvoiceMigration"` | Down-migration drops 3 columns |
| 5. Client service + VM flow | `... --filter "FullyQualifiedName~SupplierInvoiceClient"` | Revert client methods/VM state |
| 6. WPF UI (split, zoom, highlights, camera) | `... --filter "FullyQualifiedName~SupplierInvoiceClient"` + build | Revert view/dialog/wiring |

## Phase 1: Backend OCR Core

- [ ] 1.1 Packages in `Directory.Packages.props` + project refs: `Tesseract`, `OpenCvSharp4` (+`OpenCvSharp4.runtime.win`), `PDFtoImage`; confirm no vulnerabilities (`dotnet list package --vulnerable`).
- [ ] 1.2 `Core/Interfaces/IOcrEngine.cs` + records (`OcrImage`, `OcrWord`, `OcrPage`) and `Core/Common/OcrConfidence.cs` bands.
- [ ] 1.3 `Inventory.Module/Services/Ocr/ImagePreprocessor.cs`: grayscale → CLAHE → Otsu (adaptive fallback) → deskew; pure steps + synthetic-image tests.
- [ ] 1.4 `Inventory.Module/Services/Ocr/TesseractOcrEngine.cs` (spa+eng, TSV word boxes) + tessdata delivery (data pack or vendored `Backend.API/tessdata`, copy-to-output) + datapath config.
- [ ] 1.5 `Inventory.Module/Services/Ocr/PdfPageDecoder.cs` (PDFtoImage, ≤5 pages) + image decode; size/type guards shared with the endpoint.
- [ ] 1.6 Gated native smoke test (`TEST_OCR_NATIVE=1`) + CI env flag; unit tests use stubs (no natives required).

## Phase 2: Heuristic Parser

- [ ] 2.1 `Inventory.Module/Services/Ocr/InvoiceTableParser.cs`: row clustering (Y, median-height tolerance), column anchors (mapping keywords → generic fallback), word assignment, numeric regex (comma/dot), supplier-code/barcode columns when mapped.
- [ ] 2.2 Per-field confidence (minimum of contributing words; unresolved = 0) + `OcrExtractedLineDto` projection.
- [ ] 2.3 Tests with canned word-box data: template keywords win; generic fallback; tolerant numerics; multi-column rows; unresolved fields zero-confidence; empty page.

## Phase 3: Extraction Endpoint

- [ ] 3.1 `Core/Interfaces/ISupplierInvoiceService.cs` + `OcrExtractionResultDto`/`OcrExtractedLineDto`; `OcrExtractionService` orchestration (decode → preprocess → engine → parser → previews PNG + RIF/name hints).
- [ ] 3.2 `POST /api/supplier-invoices/ocr-extract` multipart (flat form fields; ≤20 MB; png/jpg/jpeg/pdf; ≤5 pages; ProblemDetails).
- [ ] 3.3 Tests: happy path with stub engine (image + PDF), unsupported type/size rejected, no persistence, Cashier 403 via TestServer.

## Phase 4: Schema + OCR Staging

- [ ] 4.1 `SupplierInvoiceLine` + DbContext + additive migration: `OcrNameConfidence`, `OcrQuantityConfidence`, `OcrUnitCostConfidence` (nullable numeric(5,2)); smoke/model tests updated.
- [ ] 4.2 `StageSupplierInvoiceRequestDto.OcrSourced` + `StageLineDto` confidence fields; staging persists confidences and skips the first-import mapping requirement when OCR-sourced (tabular path unchanged).
- [ ] 4.3 Tests: first OCR import without template; confidences persisted; tabular flow still requires mapping.

## Phase 5: Client Service + VM Flow

- [ ] 5.1 Desktop client `ISupplierInvoiceService`/`SupplierInvoiceService`: `ExtractOcrAsync` multipart upload.
- [ ] 5.2 `SupplierInvoiceViewModel.OcrReview` partial: extract → build `StageLineDto` (defaults 0 + confidence 0 when unresolved) → stage with `OcrSourced = true`; review state (previews, selected page, zoom); clear on new source/confirm.
- [ ] 5.3 Tests (headless): upload route/payload; stage flag/confidences; review state transitions; camera-degradation command path.
- [ ] 5.4 (emergent, spec "Editing clears the highlight" → "value flows to confirm as usual") Confirm corrections: `ConfirmLineDto` optional `Name`/`Quantity`/`UnitCostDocument` corrections — zero-trust validated (name non-blank ≤100 when provided, quantity/cost ≥ 0), document cost re-normalized with the invoice snapshot rate, the corrected field's OCR confidence cleared, applied inside the confirm transaction.

## Phase 6: WPF UI

- [ ] 6.1 `SupplierInvoiceView.xaml`: "Escanear factura (OCR)" + camera buttons; split layout (left previews with zoom + page navigation, right existing grid) only for OCR origin.
- [ ] 6.2 Cell highlighting: yellow 60–85 / red < 60 from persisted confidences; clears on edit.
- [x] 6.3 Camera dialog REMOVED (T10, L14): capture is file-only; client OpenCvSharp/WpfExtensions packages and the System.Drawing.Common bump reverted; OCR file button + wheel zoom kept.
- [ ] 6.4 Client tests for highlight mapping/zoom state; build + existing client tests green.

## Phase 7: Verification

- [ ] 7.1 `dotnet build CommandCenter.slnx -c Release` (0/0); full `dotnet test`; coverage `scripts/check-coverage.py` within gates.
- [ ] 7.2 Independent verification per slice + final; register ANEXO 8.147 in `docs/reporte.txt`; note migration-down rollback and native-packaging limitations.
