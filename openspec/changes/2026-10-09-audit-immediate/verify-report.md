# Verify Report: Fase inmediata de la auditoría (8.153)

## Veredicto

**PASS WITH WARNINGS** — S1 (SEC-05) y S2 (SEC-06) cumplen sus specs con evidencia reproducible; la refutación S3 (SRE-05) queda confirmada. W1 (filtración del saldo por el rechazo del vuelto, hallada por el verificador independiente) fue **cerrada por T2b** test-first (RED 1F→GREEN, suite 2144/2144). W2 registrada como limitación (authz pinada por reflexión de atributo, sin test HTTP).

## Gates de cierre (post-T2b)

- Build: `dotnet build CommandCenter.slnx -c Release` → 0 advertencias, 0 errores.
- Suite completa: **2144/2144**, 0 fallas, 0 omitidas (base 2129 + 3 T2 + 12 T3; T2b modifica un test existente sin alterar el total).
- Cobertura (corrida del verificador T4): Core **0.8886** / Sales.Module **0.8962** / Inventory.Module **0.8558** vs umbrales .70/.80/.72 — **exit 0**.
- Spot-check del padre: filtro `~CashDrawerBlindBalance` **12/12**; filtro del vuelto **1/1** (T2b RED→GREEN).

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-USJ-01 | COMPLIANT | `User.cs:17,27` con `[JsonIgnore]`; pipeline STJ sin Newtonsoft; sin serializaciones de entidad fuera de DTOs |
| REQ-USJ-02 | COMPLIANT | `UserSerializationTests` (valor y nombre ausentes case-insensitive; otros campos presentes) — 2/2 |
| REQ-BCB-01 | COMPLIANT | `CashDrawerController.cs:126-132` roles `Admin,Manager`; route/query intactos; test de reflexión + route |
| REQ-BCB-02 | COMPLIANT | mensaje exacto en `CashAdvanceCoordinator.cs:59-62` (guard intacto); test sin "Disponible"/cifras; sin efectos |
| REQ-BCB-03 | COMPLIANT | VM fail-closed (`CanViewTheoreticalBalance`), fetch condicional, "—"/"", `null` en adelanto; 12 tests |
| REQ-BCB-04 | COMPLIANT | firma `decimal?` (IDialogService/Wpf/VM), tope solo con valor, `AvailableCashDisplay`; tests |
| REQ-BCB-05 | PARTIAL (W2) | mensaje y WPF completos; authz solo por reflexión — 403/200 no observado por HTTP (harness gated a Postgres) |
| S3 (SRE-05) | COMPLIANT | resolver `:47-51` 400 / `:69-80` HIT / `:82-87` 422; missingKeyMessage en complete `:114-119`; 3 tests verdes; cliente WPF con clave; `[RequireIdempotencyKey]` inexistente en el repo |

## Hallazgos (honestos)

- **W1 — CERRADA por T2b (`7062553`)**: `CashDrawerService.RecordSaleChangeAsync` filtraba `Disponible: {saldo}` en el rechazo del vuelto (alcanzable por Cashier al cobrar); el mensaje ahora preserva el prefijo y el "Vuelto requerido" (dato del propio operador) sin el disponible. Test extendido con `DoesNotContain("Disponible")` (RED 1F→GREEN). El egreso ("para realizar el egreso", Admin-only) queda intacto por diseño.
- **W2 — limitación registrada**: la authz del endpoint queda pinada por el atributo (contrato del pipeline); sin test HTTP que observe el 403/200 real (el harness `WebApplicationFactory` del repo es gated a PostgreSQL). Recomendación G1 del ANEXO.
- **INFOs**: I1 (test positivo sin `Role`, cubierto indirectamente); I2 (el 409 enmascarado del adelanto llega al WPF como `HttpRequestException` genérica — sin fuga); I3 (binding `HasError` pre-existente roto en el XAML del diálogo); I4 (ambos archivos de auditoría versionados: `a4be0e9` del mantenedor + `e51a86a`); I5 (`security_stamp` viaja como claim JWT por diseño — fuera del JSON de SEC-05).
- **Ajustes de tests auditados (sin enmascaramiento)**: `CashDrawerClosureTests` y `CurrencyFormatLiveRefreshTests` (sesión Admin en los que asertaban saldo), `CashDrawerRbacAndPaymentMethodsTests` (firma nullable en mocks), `ProductImportExportTests` (stub nullable). Los caminos de cajero quedan cubiertos por los 12 casos nuevos.

## Artefactos / commits

Change `2026-10-09-audit-immediate` (proposal, 2 specs, tasks, state, este reporte); tracker `odd/tasks/auditoria-fase-inmediata.md`; ANEXO 8.153. Commits: `2e536fb` (plan) + `596a3ff` (T1) + `a5935bd` (T2) + `3e3b811` (T3) + `7062553` (T2b) + commit de cierre.
