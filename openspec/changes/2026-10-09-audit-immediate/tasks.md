# Tasks: Fase inmediata de la auditoría (8.153)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~450-550 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 3 unidades + T2b + cierre |
| Delivery | ask-on-risk |
| Chain | stacked-to-main (cached) |

### Suggested Work Units

| Goal / PR | Focused test command | Rollback boundary |
|---|---|---|
| 1. SEC-05 JsonIgnore (T1) | `--filter "FullyQualifiedName~UserSerialization"` | Revertir `User.cs` + test |
| 2. SEC-06 backend (T2) | `--filter "FullyQualifiedName~CashDrawerBalanceAuthorization\|~CashAdvanceCoordinator\|~DataIntegritySprint1"` | Revertir controller + coordinator + tests |
| 3. SEC-06 WPF (T3) | `--filter "FullyQualifiedName~CashDrawerBlindBalance\|~CashDrawerClosure\|~CashDrawerRbac\|~CurrencyFormatLiveRefresh"` | Revertir VM/diálogo/XAML + tests |
| 4. Verificación + cierre (T4) | suite completa + cobertura | Revertir docs |

## T1 (S1) — SEC-05: ocultar campos sensibles de `User` en serialización

- [x] 1.1 `[System.Text.Json.Serialization.JsonIgnore]` en `PasswordHash` y `SecurityStamp` (`Core/Entities/User.cs`).
- [x] 1.2 Test de serialización STJ (`CommandCenter.Tests/Unit/UserSerializationTests.cs`; valor y nombre ausentes; otros campos presentes) — RED 1F→GREEN 2/2.
- [x] 1.3 Build 0/0 + focused + suite 2129/2129; commit `596a3ff`.

## T2 (S2 backend) — SEC-06: endpoint ciego + rechazo sin cifras

- [x] 2.1 `GET /api/cashdrawer/current-balance` → `[Authorize(Roles = "Admin,Manager")]`.
- [x] 2.2 Rechazo del adelanto por efectivo insuficiente sin cifras: mensaje exacto `"Saldo de efectivo en caja insuficiente para el monto solicitado."` (guard intacto).
- [x] 2.3 Tests: reflexión del atributo (Admin,Manager) + route/query; mensaje sin "Disponible"/cifras; tests existentes por prefijo verificados (sin ajustes). RED 2F→GREEN.
- [x] 2.4 Build 0/0 + focused 52/52 + suite 2132/2132; commit `a5935bd`.
- [x] 2.5 T2b (post-verificación, W1): rechazo del vuelto sin "Disponible" (`CashDrawerService.RecordSaleChangeAsync`); test extendido (RED 1F→GREEN); suite 2144/2144; commit `7062553`.

## T3 (S2 cliente WPF) — SEC-06: cajero ciego en Caja y adelantos

- [x] 3.1 `CashDrawerViewModel.CanViewTheoreticalBalance` (fail-closed); fetch condicional; "—"/vacío; `null` al diálogo.
- [x] 3.2 `IDialogService.ShowCashAdvanceRegisterDialogAsync(..., decimal?)` + impl WPF + `CashAdvanceRegisterViewModel` nullable (`AvailableCashDisplay`, topo solo con valor) + XAML.
- [x] 3.3 12 tests nuevos (`CashDrawerBlindBalanceTests`) + 4 archivos de tests ajustados reportados (sesión Admin / firma nullable).
- [x] 3.4 Build 0/0 + focused 24/24 + suite 2144/2144; commit `3e3b811`.

## T4 (S1-S3) — Verificación + cierre

- [x] 4.1 Verificador independiente: PASS WITH WARNINGS; 7/8 COMPLIANT + BCB-05 PARTIAL (W2); W1 detectada y cerrada por T2b.
- [x] 4.2 Refutación SRE-05 replicada (resolver + tests + cliente + atributo inexistente); cross-ref SRE-04 (8.152).
- [x] 4.3 Suite 2144/2144 + cobertura Core 0.8886 / Sales 0.8962 / Inventory 0.8558 (exit 0) + build 0/0.
- [x] 4.4 `verify-report.md` + ANEXO 8.153 + cierre del tracker + commit `docs(8.153)`.

## Notes

- Tracker: `odd/tasks/auditoria-fase-inmediata.md`.
- El enmascaramiento de mensajes (adelanto + vuelto) es extensión justificada del hallazgo (cierra el sondeo del saldo); reversible por separado.
- Los writers NO commitean; el padre commitea por unidad.
