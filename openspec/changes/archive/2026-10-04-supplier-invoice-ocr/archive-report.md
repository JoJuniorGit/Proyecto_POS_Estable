# Archive Report: supplier-invoice-ocr

**Change**: 2026-10-04-supplier-invoice-ocr
**Date**: 2026-10-04 (archive entry date)
**Store**: openspec (repo-local)
**Verdict**: PASS_WITH_WARNINGS (final independent verification at T8 + consolidated remediation slice T9+T10 independently verified; 0 blockers)

## Summary

Open-source OCR digitization for supplier invoices: backend Tesseract 5 pipeline (grayscale → CLAHE → Otsu →
deskew) behind `IOcrEngine`, deterministic heuristic parsing of Tesseract word boxes (supplier mapping-template
keywords first, generic fallback, tolerant numerics with the es-VE dot-thousands rule shared with the tabular
path, conservative per-field confidence), a multipart extraction endpoint with PNG previews and best-effort
RIF/name hints (no persistence), OCR-sourced staging without a template with per-field confidences persisted
(clamped 0–100), a WPF side-by-side review (backend previews with zoom/page navigation + the editable grid
with yellow/red confidence highlighting and reviewer corrections flowing to the confirm), and **file-only
capture**: `png`, `jpg`, `jpeg`, `webp`, `bmp`, `tif`, `tiff`, `pdf`. Camera capture was removed by the
maintainer during closure (tracker L14); client OpenCvSharp/WpfExtensions packages and the
`System.Drawing.Common` pin bump were reverted.

## Implementation Slices

| Slice | Scope | Commit |
|-------|-------|--------|
| 1 | OCR core: packages, `IOcrEngine`, preprocessing, PDF raster, vendored tessdata, CI flag | `6dafd32` |
| 2 | Heuristic parser (`InvoiceTableParser`) + `OcrExtractedLineDto` | `0580da6` |
| 3 | Extraction endpoint `ocr-extract` + DTOs + DI | `1df554b` |
| 4 | Schema (3 nullable confidences) + `OcrSourced` staging | `39e5c5f` (includes T6 tests — documented process defect, L9) |
| 5 | Client upload + VM review flow | `554c419` |
| 6 | Editable corrections at confirm (T7b, zero-trust + snapshot) | `9796397` |
| 7 | WPF UI: split view, zoom, highlights, editable cells, camera (later removed) | `de3da77` |
| 8 | Remediation T9+T10: dot-thousands, clamp, wheel-zoom, camera removal, webp/bmp/tif/tiff | `1a3936b` |
| Docs | Plan `77ab69d`; closure `042c63b` + `466ff11` | — |

## Verify Report (Final State)

| Metric | Value |
|--------|-------|
| Verdict | PASS WITH WARNINGS — 0 blockers, 0 critical findings |
| Requirements | 9/9 |
| Scenarios | 28 (20 compliant + 8 PARTIAL gated/runtime) at T8; remediation slice re-verified (PASS WITH WARNINGS) |
| Tasks | 8/8 ODD tracker; SDD `tasks.md` 24/24 (see reconciliation note) |
| Build | 0 errors / 0 warnings |
| Suite at close | 1770/1770 (post-remediation; +119 vs the 8.146 baseline 1651) |
| Focused | `~SupplierInvoice\|~Ocr` 223/223 |
| Coverage (T8 closure) | Core 0.8835 / Sales.Module 0.8893 / Inventory.Module 0.8545 |
| Vulnerability gate | Clean across 10 projects (after the pin revert) |
| E2E | WPF health 2/2 (app boot + navigation incl. the OCR view) |

The archived `verify-report.md` reflects the T8 closure state; the post-closure remediation slice (T9+T10,
commit `1a3936b`) was independently verified per tracker L15 and ANEXO 8.147 section G6.

**Archive-time task reconciliation (recorded exception)**: the persisted SDD `tasks.md` still had 24 unchecked
boxes at archive time because the apply phase tracked completion in the ODD tracker instead. The orchestrator
authorized the mechanical reconciliation (24 unchecked → checked) under the Task Completion Gate exception:
completion is proven by the final verify-report, the ODD tracker rows with commits, and the 1770/1770 suite.

## Specs Synced

| Domain | Action | Result |
|--------|--------|--------|
| `supplier-invoice-ocr-extraction` | Created | 4 requirements / 11 scenarios |
| `supplier-invoice-ocr-review` | Created | 3 requirements / 7 scenarios (camera scenarios removed; additional formats added) |
| `supplier-invoice-staging` | Updated | +`OCR-Sourced Staging Without Template` (3 scenarios); `Staging Review Semantics` replaced; 4→5 requirements, 11→15 scenarios — byte prefix preserved |

Composition deviation (recorded): `gentle-ai sdd-archive-compose` is absent in the installed build
`4.0.1-0.20261003190532-0dda8895f664`; composition ran as a deterministic script with prefix-byte assertions
and requirement/scenario count equality. No destructive deltas (0 REMOVED).

## Archive Notes

- Mechanical move: pre-move recursive snapshot + per-file SHA256 readback → 0 differences (9 files).
- Residuals preserved (ANEXO 8.147 G7): gated native/Postgres tests run in CI; WPF visual runtime not
  automated (wheel-zoom compile-verified); multipage-TIFF/animated-WebP variants not exercised;
  `OcrSourced` client-provided (design D11); commit `39e5c5f` not standalone-buildable (history not
  rewritten; documented); `"4.250"` dot-thousands rule is now the es-VE convention on both paths.
- Delivered to main as part of the V0.15 → main chain PR created in this same closeout (see GitHub).
