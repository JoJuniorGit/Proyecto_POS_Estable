```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:d3cef233527d8e1ad6464e0374aeb02e19199c1c52c2e7c438b3065166122d32
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 15/15
scenarios: 31/34
test_command: dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build --nologo -v q
test_exit_code: 0
test_output_hash: sha256:ec6118dd041a3342e3b4f556626df440ff022978c0625d237533560d96ad411f
build_command: dotnet build CommandCenter.slnx -c Release --nologo -v q
build_exit_code: 0
build_output_hash: sha256:965aa62f7588b0a6fb31e7aaa6c76e540b6bec060e476ad232bd0d7163c82782
```

## Verification Report

**Change**: 2026-10-03-custody-partial-deliveries
**Version**: N/A (delta specs, no version header)
**Mode**: Standard (strict_tdd: false in `openspec/config.yaml`)

### Completeness
| Metric | Value |
|--------|-------|
| Tasks total | 25 |
| Tasks complete | 25 |
| Tasks incomplete | 0 |

### Build & Tests Execution

**Build**: ✅ Passed
```text
dotnet build CommandCenter.slnx -c Release --nologo -v q
Compilación correcta.
    0 Advertencia(s)
    0 Errores
Exit code: 0
sha256: 965aa62f7588b0a6fb31e7aaa6c76e540b6bec060e476ad232bd0d7163c82782
```

**Tests (.NET)**: ✅ 1585 passed / ❌ 0 failed / ⚠️ 0 skipped
```text
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build --nologo -v q
Correctas! - Con error: 0, Superado: 1585, Omitido: 0, Total: 1585, Duración: 1 m 5 s
Exit code: 0
sha256: ec6118dd041a3342e3b4f556626df440ff022978c0625d237533560d96ad411f
```
Baseline al inicio de 8.145: 1535 tests (ANEXO 8.144) → **+50 tests netos**.

**Tests (Web)**: ✅ 300 passed / ❌ 0 failed (65 suites, `node --test`)
**Lint (Web)**: ✅ `npm run lint` (oxlint) exit 0

**Coverage**: ✅ Above thresholds (exit 0)
```text
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --no-build
  --collect:"XPlat Code Coverage" --settings CommandCenter.Tests/coverage.runsettings
Newest artifact: CommandCenter.Tests/TestResults/<guid>/coverage.cobertura.xml
python scripts/check-coverage.py <coverage.cobertura.xml>  (exit 0)
  Core             rate=0.8805 min=0.7000 [OK]
  Sales.Module     rate=0.8895 min=0.8000 [OK]
  Inventory.Module rate=0.8428 min=0.7200 [OK]
```

**Environment note**: `TEST_POSTGRES_CONNECTION` unset in this runtime. Postgres-gated tests short-circuit by design locally and are counted as passed; their Postgres branches (backfill runtime, xmin race, idempotency unique index) were **not exercised** locally — CI covers them with the variable set.

**Parent-recorded context (not re-executed by this phase)**: per-slice independent verifications — T1 PASS WITH WARNINGS (suite 1543/1543), T2 PASS WITH WARNINGS (suite 1563/1563, tras corrección de idempotencia atómica y fixture), T3 PASS (suite 1572/1572), T4 PASS WITH WARNINGS (suite 1585/1585, tras corrección de wiring del modal WPF), T5 PASS (web 300/300, lint 0, bundle estático consistente).

### Spec Compliance Matrix

#### custody-partial-delivery
| Requirement | Verdict | Evidence |
|---|---|---|
| Delivery States | met | `SalesService.Deliveries.cs` (PartiallyDelivered/Delivered, `PickupDate` solo al completar); tests `DeliverPartial_*` ejecutados |
| Per-Line Quantities | met | `SaleItem.DeliveredQuantity numeric(18,3)` + migración; read model `PendingPickupItemDto` (total/entregada/pendiente) + agregados; tests de modelo y lista |
| Delivery Event Log | met | `SaleDelivery`/`SaleDeliveryItem` append-only con snapshot de cajero; tests de persistencia y FKs; sin endpoints update/delete |
| Validation and Atomicity | met (con reserva) | Validaciones server-side con mensajes exactos + transacción ReadCommitted + execution strategy; 409 mapeado y testeado con excepción simulada. **Carrera real de 2 transacciones no ejecutada** (Postgres-gated) |
| Idempotent Endpoint | met | `Idempotency-Key` obligatorio; replay mismo `DeliveryId` sin segundo evento; mismatch 422; Driver 403 sin persistir; registro dentro de la transacción del servicio |
| Legacy Equivalence | met | `ConfirmPickupAsync` delega a `DeliverPartialAsync` con todas las cantidades pendientes; tests legacy verdes |
| Migration and Backfill | met (código) / unverified (runtime) | Migración aditiva + SQL backfill (`DeliveryStatus = 0` → `DeliveredQuantity = Quantity`); smoke gated **no ejecutado localmente** |

#### custody-delivery-receipt
| Requirement | Verdict | Evidence |
|---|---|---|
| Note Generation | met | `DeliveryNotePdfGenerator` con hora local Venezuela, cajero, ítems, pendientes históricos (`Id <= deliveryId`, reimpresión estable), aviso no fiscal, sin datos monetarios |
| Retrieval Endpoint and Access | met | `GET .../receipt` `application/pdf`; 404 inexistente/ajena; 403 Driver; tests ejecutados |

#### custody-delivery-wpf
| Requirement | Verdict | Evidence |
|---|---|---|
| Dispatch Dialog | met | `PartialDeliveryDialogViewModel` + modal WPF; clamp, negativos, fraccionarios, `CanConfirm`, cancelación sin request; wiring corregido (1 `IsCancel`, cierre en 1 activación) |
| Partial Badge and Progress | met | Badge azul `IsPartiallyDelivered` + "Retirado: X/Y" + barra; tests de progreso, recarga parcial y remoción al completar |
| Delivery Note Print | met | Descarga de nota → PDF temporal → visor del sistema; 409 con mensaje + recarga + borrador conservado; tests |

#### custody-delivery-web
| Requirement | Verdict | Evidence |
|---|---|---|
| Dispatch Modal | met | `PartialDeliveryModal.jsx` con clamp, confirm-disabled, errores 400/409, idempotency key; tests ejecutados (SSR + helpers) |
| Partial Badge and Progress | met | Badge azul + `<progress>` + "Retirado"; helpers `clampQuantity`/`computeProgress`/`isConfirmEnabled`/`buildDeliveryPayload` con 4 tests |
| Delivery Note Print | met | Blob autenticado → pestaña nueva (`opener=null`, revoke); fallo de impresión conserva el retiro; test de descarga PDF |

### Warnings (non-blocking)

1. Postgres-gated sin ejecutar en local: backfill runtime, carrera xmin y unique index de idempotencia — cubiertos en CI (`TEST_POSTGRES_CONNECTION`).
2. Concurrencia real 409: verificada por contrato (xmin configurado + excepción simulada), no por carrera real.
3. Sin e2e DOM/interactivo de los modales Web; sin test interactivo del modal WPF (wiring validado estáticamente contra la semántica de WPF).
4. E2E WPF de salud no ejecutado en esta fase (no abre el modal).
5. Clamp Web no redondea a 3 decimales (el server redondea `numeric(18,3)`); parseo decimal dependiente de cultura (es-VE usa coma).
6. `Items[].id` → `Items[].saleItemId` en el DTO de pendientes: consumidores actuales verificados; clientes externos no auditados.
7. `GetDeliveryNotePdfAsync` es síncrono (decisión D8 aceptada).

### Rollback

- Migración `AddCustodyPartialDeliveries` aditiva; `Down` elimina tablas + columna, sin operaciones de datos.
- Cada slice es revertible por commit; el endpoint legacy `confirm-pickup` sigue funcional.
- Snapshots financieros y ventas históricas intactos (la feature no escribe campos monetarios ni inventario).

### Addendum — Remediación 2026-10-03 (post-verificación)

Commits: `b93c8b9` (crash WPF), `069a7fe` (alias DTO + tests Postgres reales), `0b4fcdf` (Web: retiro completo, redondeo/cultura, reconcile 409, bundle), `2839817` (WPF: retiro completo + E2E interactivos).

- **Crash reportado por el mantenedor**: `ProgressBar.Value` (TwoWay por defecto) sobre `ProgressPercent` readonly → `XamlParseException` al instanciar la fila → `Shutdown()` exit 0. Fix `Mode=OneWay`; regresión cubierta con el mock E2E devolviendo filas reales.
- **Botón "Retiro Completo"** en Web y WPF: reutiliza el modal/flujo con cantidades precargadas (`prefillPending` / `PendingDraft`), un solo POST idempotente.
- **Warnings cerrados**: (1) gated Postgres ejecutados localmente contra `pos_test` real (40/40; unique index 23505, xmin con rollback, backfill real) — CI sigue siendo la cobertura canónica; (2) e2e interactivos WPF 4/4 FlaUI + Web 3/3 Playwright; (3) E2E WPF completo 18/18; (5) redondeo a 3 decimales + coma decimal; (6) alias `id` de compatibilidad.
- **Evidencia de la remediación**: build Release 0/0; suite .NET 1591/1591; E2E WPF 18/18; web 301/301 + lint 0 + Playwright 3/3; bundle regenerado con referencias consistentes.
- **Residuales aceptados**: el E2E WPF descarta el diálogo de impresión sin assertar su aparición; el mock E2E responde siempre `Delivered` (permanencia parcial cubierta por unit tests); `<input type="number">` web puede sanear la coma según locale (el helper la soporta; e2e cubre punto); `Process.Start` real del visor PDF no se ejercita (opener inyectado, patrón pre-existente).
