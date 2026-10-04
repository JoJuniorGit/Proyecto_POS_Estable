# Exploration: Supplier Invoice OCR Digitization

Date: 2026-10-04 · Orchestrator read-only mapping (session evidence + targeted greps); no code changes.

## Current state (evidence)

- Supplier invoice module (8.144/8.146) is WPF-only: capture starts at `SupplierInvoiceViewModel.SelectFileAsync` with a tabular filter (`*.xlsx;*.csv;*.xml`), parsing client-side, explicit column mapping UI, then `POST /api/supplier-invoices` staging.
- `UpsertColumnMappingAsync` (`Inventory.Module/Services/SupplierInvoiceService.Mapping.cs:12`) requires a confirmed mapping on a supplier's **first** import (`"A confirmed column mapping is required for a supplier's first import."`) → an OCR-sourced staging path (no columns to map) needs an explicit discriminator.
- Staging grid + VM already provide the editable review surface (`Desktop.Client/Views/SupplierInvoiceView.xaml`, `SupplierInvoiceViewModel(.Staging).cs`) with `ViewModelProxy` (Freezable) column bindings and margin recalc; the split view can extend it.
- `IDialogService` / `WpfDialogService` and `IFilePickerDialog` abstract dialogs/pickers → camera dialog and extended picker fit the established patterns.
- Client stack: `net10.0-windows`, CommunityToolkit.Mvvm, MaterialDesign, ZXing.Net bindings (barcode decoding only — **no camera capture anywhere in the repo**).
- Backend: `net10.0`, EF Core 10 + Npgsql; central package management (`Directory.Packages.props`) with explicit vulnerability pins and a CI `dotnet list package --vulnerable` gate.
- CI runs on **windows-2025** (`ci.yml`): native OCR libs ship via NuGet runtime packages on Windows — no apt/system installs needed; the suite already expects `net10.0-windows` testhost.
- RDD off (clone-local). Suite baseline 1651/1651. Next ANEXO: 8.147.

## Implications

- OCR engine + preprocessing + parsing belong to the backend per the user's criteria; the client captures (file/camera), uploads, and renders the review UI.
- Engine must sit behind an interface: unit tests run deterministically on canned TSV/box data; only a gated smoke test needs the native engine.
- Tesseract word boxes (TSV) are the parser input: rows by Y-clustering, columns by X-alignment against supplier-mapping keyword columns with a generic fallback.
- Per-field confidence must be persisted on the staged line for the grid to highlight after staging (additive columns; `numeric(5,2)` nullable, 0–100).
- Previews should be rendered by the backend (uniform for image/PDF, shows what OCR saw); the client needs no PDF stack. Camera is the only new client native dependency (OpenCvSharp4, documented size trade-off).
- tessdata (spa+eng) delivery is an implementation verification item: data NuGet package if available, otherwise vendored folder copied to output.
