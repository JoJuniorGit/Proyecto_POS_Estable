# Facturas de Proveedor — Fix: reset accidental de la Tasa de Cambio al abrir/guardar el modal "Actualizar"

Objetivo: corregir la desincronización de estado que revertía la Tasa aplicada corregida a mano al usar el botón "Actualizar" (modal "Resolver producto desde factura"): aislamiento de la cabecera, UI como fuente de verdad e inyección de la tasa viva en modal/grilla. Tramo 8.178 (bugfix). Branch: V0.15. Entrega: work unit local (push/PR = decisión del mantenedor). RDD: off (clone-local). Tests: carril `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj -c Release --filter "FullyQualifiedName~SupplierInvoice"`; build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: pedido del mantenedor (L1 verbatim en el Log).

- **S1 (Aislamiento de la cabecera)**: "La acción del botón Actualizar… debe limitarse estrictamente al contexto de la fila seleccionada. Bajo ninguna circunstancia debe disparar una recarga, re-asignación o re-escaneo de las propiedades de la cabecera (Tasa aplicada o Moneda)."
- **S2 (UI como fuente de la verdad)**: "Al invocar el modal o recargar los datos del producto vinculado, el servicio debe inyectar el valor actual que está escrito en el TextBox de Tasa aplicada (el estado transitorio de la UI), en lugar de leer el valor histórico guardado en el objeto original del escaneo."
- **S3 (Recálculo inmediato en el modal)**: "Cuando el modal de vinculación se abra, los precios proyectados (Detal/Mayor) dentro del modal deben calcularse utilizando la tasa corregida manualmente".

## Diagnóstico (L2)

Tres vectores combinados, todos en el flujo `UpdateLinkedProductAsync → LoadStagedInvoice(updatedInvoice)`:
1. **Re-alineación de moneda** (introducida en 8.176): `LoadStagedInvoice` asignaba `SelectedCurrency = invoice.Currency` en cada recarga → viola S1 (re-asignación de Moneda por la acción de fila).
2. **Revert de la tasa por la rama OCR**: la re-asignación disparaba `OnSelectedCurrencyChanged`, cuya rama 8.172 reponía `AppliedRateText = OcrDetectedExchangeRate` (el valor original erróneo) → el revert literal reportado.
3. **Snapshot con valor histórico**: la recarga dejaba `StagedInvoice.AppliedRate` con la tasa del staging (78.45) en lugar de la tasa viva del TextBox.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| F1 | S1 | inline | Quitar la re-alineación de moneda de `LoadStagedInvoice` (aislamiento; comentario 8.178) | hecho |
| F2 | S2, S3 | inline | `PropagateCurrencyAndRateToLines()` al final de `LoadStagedInvoice` (tasa viva → filas + snapshot; Bs.S sin tasa → filas 0 y confirm bloqueado) | hecho |
| F3 | S1, S2 | inline | Guarda de corrección manual: flags `_isRateManuallyEdited`/`_isApplyingProgrammaticRate` + `SetAppliedRateTextProgrammatically`; la rama OCR de `OnSelectedCurrencyChanged` solo repone si el revisor no editó; scan nuevo re-basa | hecho |
| F4 | S1–S3 | inline (+ verify) | Tests (RED 3/3 → GREEN) + actualización de 5 tests con expectativa vieja (flujo real con moneda preseleccionada) + build/suite + verificación independiente + ANEXO 8.178 + commit | hecho — build slnx 0/0; carril SupplierInvoice 286/286; suite completa Unit 1890/1890 + resto 571/571 = 2461/2461; verificación independiente PASS (S1–S3 CONFORME, 0 blockers, 2 residuales cosméticos); ANEXO 8.178; commits `1bd9f12` (fix) + docs(8.178) en V0.15 |

Estrategia de entrega: `ask-on-risk` → work unit local en V0.15. Forecast ~200 líneas autoradas (fix ~70 + tests ~180).

## Log

- L1 (2026-10-13) — Pedido del mantenedor, verbatim:

> Reseteo accidental de la "Tasa de Cambio" al abrir el modal de vinculación de productos (Actualizar)
> Objetivo / Descripción del Defecto: Corregir la desincronización de estado en la vista de Facturas de Proveedores. Actualmente, si el OCR detecta una tasa de cambio errónea y el usuario la corrige manualmente en la cabecera, al presionar el botón Actualizar en cualquier fila para vincular un producto, el sistema recarga el contexto y revierte la Tasa aplicada al valor original erróneo, borrando el trabajo del usuario.
> Criterios de Aceptación (Solución Esperada): 1. Aislamiento del Estado de la Cabecera: La acción del botón Actualizar (que abre el modal "Resolver producto desde factura") debe limitarse estrictamente al contexto de la fila seleccionada. Bajo ninguna circunstancia esta acción debe disparar una recarga, re-asignación o re-escaneo de las propiedades de la cabecera (Tasa aplicada o Moneda). 2. La UI como Fuente de la Verdad (Source of Truth): Al invocar el modal o recargar los datos del producto vinculado, el servicio debe inyectar el valor actual que está escrito en el TextBox de Tasa aplicada (el estado transitorio de la UI), en lugar de leer el valor histórico guardado en el objeto original del escaneo. 3. Recálculo Inmediato en el Modal: Cuando el modal de vinculación se abra, los precios proyectados (Detal/Mayor) dentro del modal deben calcularse utilizando la tasa corregida manualmente por el usuario en el paso anterior, garantizando coherencia visual en toda la pantalla.

- L2 (2026-10-13) — Diagnóstico con reproducción empírica: los 3 vectores del apartado Diagnóstico, con RED observado en `SupplierInvoiceRatePersistenceTests` (3/3 fallando por las causas correctas antes del fix: revert a "78.45" al volver a Bs.S, re-alineación USD→Bs.S en el save, snapshot con 78.45). Decisión: la cabecera (Moneda/Tasa) es propiedad del revisor y los cargues/recargues NO la re-asignan; la interpretación viva se propaga a filas y snapshot. Supuesto: la protección de la corrección manual se re-basa con cada escaneo nuevo (el OCR más reciente es la referencia) y se limpia en `ClearOcrReviewState`.

- L3 (2026-10-13) — Cierre: fix F1–F3 aplicado (aislamiento de cabecera en `LoadStagedInvoice`, propagación viva al final del cargue, guarda `_isRateManuallyEdited` con escritura programática unificada) + `SupplierInvoiceRatePersistenceTests` (3 casos) + 5 tests actualizados. Evidencia: RED 3/3 → GREEN; carril SupplierInvoice 286/286; suite completa 2461/2461; build slnx 0/0. Verificación independiente (agente reviewer, read-only): S1–S3 CONFORME, ciclo de vida del flag CONFORME, 0 blockers; residuales aceptados: `SyncStagedSnapshot` conserva tasa previa en snapshot con tasa viva inválida (cosmético, pre-existente 8.176) y prefill de carga con tasa del staging (idéntico a HEAD). ANEXO 8.178 registrado; commits fix + docs(8.178) en V0.15. WIP ajeno intacto en los 3 archivos compartidos (staging por hunks con parches `*.wip.patch`).
