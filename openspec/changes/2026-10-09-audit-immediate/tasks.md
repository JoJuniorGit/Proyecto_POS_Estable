# Tasks: Fase inmediata de la auditoría (8.153)

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated lines | ~450-550 |
| Budget risk | Medium-High |
| Chained PRs | Yes (continúa la cadena V0.15) |
| Split | 3 unidades + cierre |
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
- [ ] 1.1 `[System.Text.Json.Serialization.JsonIgnore]` en `PasswordHash` y `SecurityStamp` (`Core/Entities/User.cs`).
- [ ] 1.2 Test de serialización STJ: el JSON no contiene los valores ni los nombres de esas propiedades; el resto de campos sí (nuevo `CommandCenter.Tests/Unit/UserSerializationTests.cs`).
- [ ] 1.3 Build 0/0 + focused + suite; commit `fix(8.153)`.

## T2 (S2 backend) — SEC-06: endpoint ciego + rechazo sin cifras
- [ ] 2.1 `GET /api/cashdrawer/current-balance` → `[Authorize(Roles = "Admin,Manager")]`.
- [ ] 2.2 Rechazo del adelanto por efectivo insuficiente sin cifras: mensaje exacto `"Saldo de efectivo en caja insuficiente para el monto solicitado."` (guard intacto).
- [ ] 2.3 Tests: cashier → 403 en el endpoint; Admin/Manager → 200; el rechazo del adelanto no contiene "Disponible"/cifras. Tests existentes por prefijo ("Saldo de efectivo en caja insuficiente") verificados sin ajustes o ajustados y reportados.
- [ ] 2.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.153)` (lo hace el padre).

## T3 (S2 cliente WPF) — SEC-06: cajero ciego en Caja y adelantos
- [ ] 3.1 `CashDrawerViewModel.CanViewTheoreticalBalance` (Admin||Manager; fail-closed sin sesión); `LoadSessionAsync` no fetchea para no-supervisores y muestra "—"/vacío; `ProcessCashAdvanceAsync` pasa `null` al diálogo.
- [ ] 3.2 `IDialogService.ShowCashAdvanceRegisterDialogAsync(..., decimal? availableCashLocal)` + impl WPF + `CashAdvanceRegisterViewModel` nullable (`AvailableCashDisplay`, tope solo con valor) + XAML.
- [ ] 3.3 Tests nuevos (cajero ciego: sin fetch, "—", diálogo sin tope; supervisor sin cambios) + tests existentes ajustados (sesión Admin en closure/format; firma nullable en mocks/fakes) reportados nombradamente.
- [ ] 3.4 Build 0/0 + focused + suite; evidencia RED/GREEN; commit `fix(8.153)` (lo hace el padre).

## T4 (S1-S3) — Verificación + cierre
- [ ] 4.1 Verificador independiente: authz del endpoint, mensaje enmascarado, blinds del cliente, tests ajustados auditados.
- [ ] 4.2 Refutación SRE-05 documentada (resolver + tests + cliente + atributo inexistente); cross-ref SRE-04 (8.152).
- [ ] 4.3 Suite completa + cobertura + build 0/0.
- [ ] 4.4 `verify-report.md` + ANEXO 8.153 + cierre del tracker + commit `docs(8.153)`.

## Notes
- Tracker: `odd/tasks/auditoria-fase-inmediata.md`.
- El enmascaramiento del mensaje del adelanto (2.2) es extensión justificada del hallazgo (cierra el sondeo del saldo); reversible independiente.
- Los writers NO commitean; el padre commitea por unidad.
