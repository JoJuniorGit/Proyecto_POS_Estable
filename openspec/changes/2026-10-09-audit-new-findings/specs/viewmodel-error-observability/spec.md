# Spec (delta): Observabilidad de catches operativos en ViewModels — CLEAN-06 (8.152)

Fuente: auditoría re-emitida 2026-10-08, hallazgo CLEAN-06: "Reemplazar cada `catch { }` por registro en `ClientStateLogger.LogError(ex)` y propagar un mensaje de notificación al usuario a través de `IDialogService`" en los 4 archivos nombrados. Las referencias de línea de la auditoría tienen drift respecto del árbol actual; la enumeración vigente es la de esta spec (líneas a la fecha del plan; la identidad mandatoria es método + catch).

## ADDED Requirements

### Requirement: REQ-VMEO-01 — Catches operativos con log y notificación

En `Desktop.Client.Core/ViewModels/BaseViewModel.cs`, `InventoryViewModel.cs`, `InventoryViewModel.Operations.cs` y `ProductDialogViewModel.cs`, todo catch que capture una excepción de OPERACIÓN MUST registrar `ClientStateLogger.LogError($"<contexto>: {ex.Message}", nameof(<ViewModel>))`, y MUST conservar/proveer notificación vía `IDialogService` cuando la operación es disparada por el usuario.

Sitios exactos (14):

| Archivo | Línea | Método | Acción |
|---|---|---|---|
| BaseViewModel.cs | 74 | OnFatalErrorResetInternalAsync (ex. no fatal) | SOLO log (camino de fondo; sin diálogo) |
| BaseViewModel.cs | 91 | SafeInitializeAsync (ex. no fatal) | SOLO log |
| InventoryViewModel.cs | 205 | Refresh | log (diálogo existente intacto) |
| InventoryViewModel.cs | 302 | LoadDataAsync | log (diálogo existente intacto) |
| InventoryViewModel.cs | 380 | MergeProductsAsync | log (diálogo existente intacto) |
| InventoryViewModel.Operations.cs | 32 | OnProductItemChangedAsync | log (warning existente intacto) |
| InventoryViewModel.Operations.cs | 59 | TogglePauseProduct | log |
| InventoryViewModel.Operations.cs | 80 | RestoreProduct | log |
| InventoryViewModel.Operations.cs | 128 | DeleteProduct | log |
| InventoryViewModel.Operations.cs | 153 | OpenAddProduct | log |
| InventoryViewModel.Operations.cs | 189 | EditProduct | log |
| InventoryViewModel.Operations.cs | 228 | AdjustStock | log |
| InventoryViewModel.Operations.cs | 250 | Scan | log |
| ProductDialogViewModel.cs | 249 | carga de metadatos (carga inicial del diálogo) | reemplazar `Debug.WriteLine` por log + `_dialogService?.ShowWarning(...)` |

Permitidos como silenciosos (NO son hallazgo; documentar con comentario si aporta claridad):

- `catch (ObjectDisposedException)` / `catch (AggregateException)` de carreras de cancelación/dispose de CTS.
- `catch (OperationCanceledException)` de cancelación cooperativa.
- `catch (SemaphoreFullException)` de release de semáforo.

`PosViewModel.cs`: ya conforme (L235/L284/L329/L400 loguean + notifican; swallows tipados de ciclo de vida) — sin cambios; se documenta como conforme.

#### Scenario: Fallo operativo deja rastro

- GIVEN un servicio fallando y un log de cliente limpio
- WHEN se ejecuta una operación con catch operativo (p. ej. `Refresh` del InventoryViewModel)
- THEN el log de `ClientStateLogger` contiene el contexto y `ex.Message`, y la notificación al usuario se conserva.

#### Scenario: Metadatos del diálogo fallan

- GIVEN `GetParentsAsync` falla
- WHEN se abre el `ProductDialogViewModel`
- THEN queda registro en `ClientStateLogger` y se muestra advertencia por `IDialogService`; el diálogo permanece usable.

#### Scenario: Cancelación no genera ruido

- GIVEN una cancelación cooperativa (token cancelado)
- WHEN se dispara el catch de `OperationCanceledException`
- THEN no se registra error nuevo (comportamiento actual preservado).

### Requirement: REQ-VMEO-02 — Cobertura de pruebas

- Al menos 1 test representativo por archivo tocado, patrón `CartCommitResilienceTests` (leer `ClientStateLogger.ResilienceLogPath`).
- Verificación estática por el verificador: enumeración completa de catches de los 4 archivos y clasificación (operativo → log; ciclo de vida → silencioso tipado).
