```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: HEAD de3da77 (branch V0.15)
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 9/9
scenarios: 28 (20 COMPLIANT, 8 PARTIAL, 0 UNTESTED, 0 FAILING)
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --nologo
test_result: 1756/1756 passed, 0 failed, 0 skipped (exit 0)
focused_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice|FullyQualifiedName~Ocr" --nologo
focused_result: 209/209 passed
build_command: dotnet build CommandCenter.slnx -c Release
build_result: 0 warnings / 0 errors (exit 0)
coverage_command: python scripts/check-coverage.py CommandCenter.Tests/TestResults/3ea0a5f6-978c-4d0e-85a1-8019ae688e1c/coverage.cobertura.xml (exit 0)
coverage: Core 0.8835 / Sales.Module 0.8893 / Inventory.Module 0.8545
vulnerability_gate: clean across all 10 projects (dotnet list package --vulnerable --include-transitive)
e2e: E2EAppHealthTests 2/2 (parent-run, 43 s — app boot + navigation incl. the OCR-modified view)
note: sha256 de salidas no recopilados; evidencia = salidas observadas por el verificador final independiente y por las verificaciones por slice; ANEXO 8.147 en docs/reporte.txt.
```

## Verification Report

**Change**: 2026-10-04-supplier-invoice-ocr
**Version**: N/A (delta specs, no version header)
**Mode**: Standard (strict TDD per slice; RDD OFF clone-local — verificación comisionada por el orquestador)

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 8 (T1–T7, T7b emergente) |
| Tasks complete | 8 |
| Tasks incomplete | 0 |

### Build & Tests Execution

**Build**: ✅ `dotnet build CommandCenter.slnx -c Release` — 0 Advertencias, 0 Errores (WPF/XAML incluido)

**Tests**: ✅ 1756/1756 (baseline 1651 de 8.146 + 105 netos). Focused `~SupplierInvoice|~Ocr`: 209/209.

**Coverage**: ✅ Core 0.8835 / Sales.Module 0.8893 / Inventory.Module 0.8545 — exit 0.

**Environment note**: `TEST_OCR_NATIVE` y `TEST_POSTGRES_CONNECTION` sin setear en local → los branches gated retornan en silencio (no skip); CI exporta `TEST_OCR_NATIVE: "1"`. El smoke nativo real se ejecutó en T2 (`words=4: FACTURA | PROVEEDOR | TOTAL | 12345`). E2E de salud 2/2 (contexto del padre).

### Spec Compliance Matrix

#### supplier-invoice-ocr-extraction (4 requirements / 11 scenarios)

| Requirement | Scenario | Pinning test(s) | Result |
|-------------|----------|-----------------|--------|
| OCR Engine and Image Preprocessing | Preprocessing pipeline steps | `OcrImagePreprocessorTests.*` (binarize Otsu/adaptativo, deskew ±) | ✅ COMPLIANT |
| OCR Engine and Image Preprocessing | Engine behind abstraction | stub-engine tests (extraction/parser); `OcrNativeEngineSmokeTests` | ⚠️ PARTIAL (smoke nativo gated local; CI con flag; real OK en T2) |
| Template-Aware Column Extraction | Template keyword columns win | `OcrTableParserTests.TemplateKeywords_TakePriority…`; `OcrExtractionServiceTests.ExtractAsync_WithColumnMapping…` | ✅ COMPLIANT |
| Template-Aware Column Extraction | Generic fallback | `GenericKeywords_ResolveColumns_WithoutTemplate`; `HeaderRow_WithMostMatchedColumns_Wins` | ✅ COMPLIANT |
| Template-Aware Column Extraction | Tolerant numeric parsing | `TolerantNumericFormats_ParseLikeFilePath` (`$ 4.250`→4.25 espejo documentado) | ✅ COMPLIANT (residual L6a) |
| Confidence Scoring | Field confidence aggregation | `FieldConfidence_IsMinimumOfContributingWords_WithOneDecimal`; `MissingNumericFields_YieldNullValuesAndZeroConfidence` | ✅ COMPLIANT |
| OCR Extraction Endpoint | Extract from an image upload | `OcrExtractionEndpointTests.OcrExtract_AdminUploadsImage_…` (TestServer) | ✅ COMPLIANT |
| OCR Extraction Endpoint | PDF multi-page extraction | `OcrExtractionServiceTests.ExtractAsync_Pdf_…`; `OcrPageDecoderTests` (3/5/6 páginas) | ⚠️ PARTIAL (rama HTTP del PDF no ejercida) |
| OCR Extraction Endpoint | Unsupported input rejected | `OcrExtract_UnsupportedType/OversizedFile/EmptyFile…` | ✅ COMPLIANT |
| OCR Extraction Endpoint | Cashier blocked | `OcrExtract_Cashier_ReturnsForbiddenWithoutProcessing` (403, engine no llamado) | ✅ COMPLIANT |
| OCR Extraction Endpoint | Supplier hint is best-effort only | `ExtractAsync_Image_ReturnsLinesWithConfidencesPreviewsAndHints`; `…NullHints` | ✅ COMPLIANT |

#### supplier-invoice-staging (delta: 1 ADDED + 1 MODIFIED / 9 scenarios)

| Requirement | Scenario | Pinning test(s) | Result |
|-------------|----------|-----------------|--------|
| OCR-Sourced Staging Without Template | First OCR import without template | `StageAsync_OcrSourcedFirstImportWithoutMappingPersistsDraft`; `…WithMappingStillUpsertsMapping` | ✅ COMPLIANT |
| OCR-Sourced Staging Without Template | Confidence persisted on lines | `StageAsync_OcrSourcedPersistsFieldConfidences`; `…TabularStagingKeepsConfidencesNull`; smoke model | ✅ COMPLIANT |
| OCR-Sourced Staging Without Template | Tabular flow untouched | `StageAsync_TabularFirstImportWithoutMappingStillThrows`; mapping save/reuse tests | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | Unmatched line is a creation candidate | `StageAsync_ClassifiesCostDifferencesAsUpdateAndUnmatchedAsNew`; client `UnresolvedLine_ShowsNuevoBadge…` | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | Zero-cost product classifies as update | same classification test (zero-cost → Update) | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | Status classification uses normalized cost | `…ClassificationComparesNormalizedCost…`; `…EquivalentUsdCostsAtDifferentRates…` | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | Instant client-side recalc unchanged | `MarginEdit_RecalculatesSuggestedPriceWithoutCallingApi` (strict mock) | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | No apply before confirm | `StageAsync_DoesNotApplyCostMarginOrStockBeforeConfirm`; `ConfirmAsync_AppliesOnlyApprovedResolvedLines` | ✅ COMPLIANT |
| Staging Review Semantics (MODIFIED) | Low-confidence cells are highlighted | `NameBand_MapsOcrConfidenceThresholds` (59.99/60/84.99/85); `EachFieldBand_ReadsItsOwnConfidenceField`; `EditingField_ClearsOnlyItsBandAndRevertingRestoresIt`; XAML `#FFF3C4`/`#FFD6D6` | ✅ COMPLIANT (render runtime = partial visual) |

#### supplier-invoice-ocr-review (3 requirements / 8 scenarios)

| Requirement | Scenario | Pinning test(s) | Result |
|-------------|----------|-----------------|--------|
| Document Capture | Attach an image or PDF | `ScanInvoice_HappyPath_…`; `ExtractOcrAsync_PostsMultipartFile…`; `…OmitsOptionalFields` | ✅ COMPLIANT (headless) |
| Document Capture | Camera capture | `ScanCamera_WhenDialogReturnsPath_…`; servicio Try* | ⚠️ PARTIAL (dispositivo/dialog runtime no ejecutado) |
| Document Capture | No camera available | `ScanCamera_WhenDialogReturnsNull_…`; mensaje inline del diálogo | ⚠️ PARTIAL (runtime no ejecutado; ruta de archivo intacta por L4) |
| Side-by-Side Staging Review | Split layout for OCR origin | XAML `IsOcrSource` (pane izquierdo + grilla); `LoadStagedInvoice_SetsEditorEnabledOnlyForOcrOrigin` | ⚠️ PARTIAL (render estático) |
| Side-by-Side Staging Review | Zoom | `OcrZoom_ClampsToRangeAndCommandsStepAndReset`; `OcrPageNavigation_…`; `ScaleTransform` OneWay | ⚠️ PARTIAL (visual no ejecutado) |
| Side-by-Side Staging Review | Non-OCR invoice unchanged | `TabularFileSelection_ClearsOcrReviewState`; `StageInvoiceAsync_TabularFlow_KeepsMappingAndOcrSourcedFalse` | ⚠️ PARTIAL (visibilidad estática) |
| Low-Confidence Verification Before Approval | Highlight follows persisted confidence | band tests + XAML triggers | ⚠️ PARTIAL (color renderizado no ejecutado) |
| Low-Confidence Verification Before Approval | Editing clears the highlight | `EditingField_ClearsOnlyItsBand…`; `Confirm_OcrLine_SendsCorrectionsOnlyForChangedFields`; `ConfirmAsync_AppliesCorrectionsAndClearsCorrectedFieldConfidences` | ✅ COMPLIANT |

**Compliance summary**: 20/28 scenarios fully compliant; 8 PARTIAL (gated native smoke, rama PDF por HTTP, y 6 runtime cámara/visuales); 0 UNTESTED; 0 FAILING; 9/9 requirements met.

### Cross-Slice Integration

| Hop | Named evidence |
|-----|----------------|
| capture/upload | `ExtractOcrAsync_PostsMultipartFileWithSupplierAndFullMappingFields`; `ScanInvoice_HappyPath_…` |
| extract (parser+confidences) | `OcrTableParserTests`; `OcrExtractionServiceTests.ExtractAsync_Image_…`; endpoint TestServer |
| OCR-sourced stage (no template) | `StageAsync_OcrSourcedFirstImportWithoutMappingPersistsDraft`; `captured.OcrSourced=true, ColumnMapping=null` |
| confidence persisted | `StageAsync_OcrSourcedPersistsFieldConfidences` |
| grid bands + edits | `NameBand_MapsOcrConfidenceThresholds`; `EditingField_ClearsOnlyItsBandAndRevertingRestoresIt` |
| confirm corrections (snapshot) | `ConfirmAsync_AppliesCorrectionsAndClearsCorrectedFieldConfidences`; `ConfirmAsync_NormalizesCorrectedBsSCostWithInvoiceSnapshotRate` |

(No single test chains all hops — expected; each hop is individually pinned.)

### Residual Risks (accepted)

1. Gated tests retornan en silencio local (`TEST_OCR_NATIVE`, `TEST_POSTGRES_CONNECTION`); CI los ejecuta.
2. `$ 4.250` → 4.25 (espejo intencional del parseo tabular; ambigüedad es-VE documentada para el mantenedor).
3. Confianzas persistidas sin clamp server-side 0–100 (solo display).
4. `OcrSourced` es client-provided (D11); money/apply siguen zero-trust.
5. Runtime visual/cámara real sin automatización (compile + estática + E2E de arranque); temp JPGs de cámara no se borran; wheel-zoom omitido (spec admite botones); code-behind del diálogo sancionado por D5/D13.
6. `HasReadableIdentifier` del cliente duplica `IsReadableLine` del backend (sobre-bloqueo seguro; riesgo de deriva).
7. Commit `39e5c5f` (T5) no compila standalone (incluye tests de T6; defecto de proceso documentado en L9; sin reescritura de historia).
