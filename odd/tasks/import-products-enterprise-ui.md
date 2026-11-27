# Import Products Enterprise UI

Branch: V0.15. Delivery: local work-unit commit; no push. Test runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build`.

## Specs

S1. Header: replace the English title with `IMPORTACIÓN Y EXPORTACIÓN DE PRODUCTOS`, add a 42x42 `#EDE9FE` badge with an import icon, title `Importación y Exportación de Productos` (22px bold `#0F172A`) and the requested subtitle (13px `#64748B`).

S2. Stepper: show `1. Cargar archivo`, `2. Revisar vista previa`, `3. Guardar cambios`; active circle `#6D28D9`, completed check `#16A34A`, pending `#E2E8F0`/`#94A3B8`; derive states from the ViewModel without changing the import flow.

S3. Toolbar card: primary import button `#6D28D9`/hover `#5B21B6`; sober export button `#ECFDF5`/`#A7F3D0`/`#047857`/icon `#059669`; neutral template button; separator; interactive Merge pill with ToggleSwitch and the requested off/on copy; file chip with name, full-path tooltip, and discard action.

S4. Preview KPIs: `Todas (X)`, `Válidas (Y)`, `Con errores (Z)` filter the bound grid without mutating the source collection; local search filters by SKU or name; invalid rows remain highlighted.

S5. Main card: guided empty state with drag/drop for `.xlsx` and `.csv`, enterprise DataGrid with the 14 specified columns, frozen SKU/Name, recycling virtualization, no external ScrollViewer, thin scrollbars, and an error detail strip for the selected invalid row.

S6. Footer: fixed confirmation bar with clear state text, `Limpiar`, and `Guardar en Base de Datos (Y)`; disabled colors `#E2E8F0`/`#94A3B8`, enabled `#6D28D9`/hover `#5B21B6`, busy text `Guardando...`.

S7. Preserve Excel/CSV parsing, validation, API contracts, commands, and existing column-mapping workflow.

S8. Tests cover KPI filters, local search, clearing preview, Merge state, and save enablement. Build must be 0 warnings/0 errors and the full suite green.

## Tasks

- [x] T1 (S1-S2) - Extend the ViewModel with stepper and derived UI state. Inline.
- [x] T2 (S3-S6) - Refactor the XAML into header, toolbar, main card, grid, and footer. Inline.
- [x] T3 (S5) - Update drag/drop code-behind for `.xlsx`/`.csv` and visual drag state. Inline.
- [x] T4 (S8) - Add ViewModel tests. Inline.
- [x] T5 (S8) - Build, run full tests, and inspect structural/DPI layout. Inline.

## Log

L1. User requested a complete Masterchip-level visual refactor of `IMPORT PRODUCTS` while preserving Excel/CSV logic, API contracts, and bindings; requirements include enterprise header, 3-step stepper, toolbar card, Merge pill, file chip, KPI filters, local search, guided empty state, enterprise grid, error strip, and fixed footer.

L2. Implemented `FilteredProducts`, KPI/search filtering, file-chip state, stepper state, selected-error detail, Merge copy, save-button text, busy state, collection-change refresh, and CSV/XLSX drag handling. The column-mapping workflow and commit command remain intact.

L3. Evidence: `dotnet build CommandCenter.slnx -c Release` = 0 warnings / 0 errors; full PostgreSQL-backed suite = 4539/4539; WPF harness rendered populated, error-selected, Merge-on, empty, and scrolled states at 96 DPI and populated state at 120 DPI. Currency headers fit without truncation after width adjustments.
