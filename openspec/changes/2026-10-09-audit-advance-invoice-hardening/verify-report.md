# Verify Report: Hardening de adelantos y facturas (8.154)

## Veredicto

**PASS WITH WARNINGS** — 5/5 requisitos COMPLIANT (REQ-CAI-01/02/03, REQ-SIA-01/02); sin CRITICAL. W1 registrado (cliente Web envía campos muertos; sin impacto de seguridad).

## Gates de cierre

- Build: `dotnet build CommandCenter.slnx -c Release` → **0 advertencias, 0 errores**.
- Suite completa: **2162/2162**, 0 fallas, 0 omitidas (base 2144 + 11 T1 + 1 T2 + 6 T3).
- Cobertura (verificador T4): Core **0.8886** / Sales.Module **0.8962** / Inventory.Module **0.8559** vs umbrales .70/.80/.72 — **exit 0**.
- Focused reproducidos por el verificador: T1 33/33, T2 111/111, T3 31/31.

## Veredicto por requisito (verificador independiente read-only)

| Requisito | Veredicto | Evidencia clave |
|---|---|---|
| REQ-CAI-01 | COMPLIANT | `CashDrawerController.cs:340-348` guard exacto (401 `"Sesión inválida."`; `UnauthorizedAccessException("Usuario no encontrado.")` → 403; identidad desde `GetUserAsync` del token); DTO sin campos legacy; cliente WPF sin identidad en el body; seam del test prueba token vs body y `GetUserNameByIdAsync` `Times.Never`. |
| REQ-CAI-02 | COMPLIANT | `CashDrawerController.cs:351` ancla antes del coordinador (`:359` pasa `anchoredRate`); semántica A5 del privado intacta (`git show d7616ac`); escenarios nuevos: 67→60 (ancla), 63.50 (dentro), 120→"excede" sin efectos, 80.463→80.47 (fail-open). |
| REQ-CAI-03 | COMPLIANT | `CashDrawerCashAdvanceSecurityTests` (suplantación/401×2/403/pin DTO/pin STJ) + arnés A5 extendido; ajustes auditados sin enmascaramiento. |
| REQ-SIA-01 | COMPLIANT | `SupplierInvoiceApplyOrdering.cs` exacto (asc `ResolvedProductId`, nulls primero, tie-break `Id`); único cambio del loop (`Apply.cs:82`); efectos idénticos. |
| REQ-SIA-02 | COMPLIANT | 5 tests puros (desordenada/nulls+ties/invertida/vacía/única) + regresión con orden descendente (efectos + orden de locks por `movements[0]`). |

## Hallazgos (honestos)

- **W1**: `Web.Frontend/src/components/register/CashAdvanceModal.jsx:137-146` sigue enviando `cashierId`/`userName` (y el bundle `wwwroot/assets/RegisterPage-*.js` los replica). Sin impacto de seguridad (STJ ignora miembros desconocidos; la identidad sale del token) y fuera del alcance del spec (T2 fue WPF), pero queda limpieza pendiente + regeneración de bundle — recomendación G1.
- **INFOs**: I1 (403 del middleware pinado por unit + estático, sin test HTTP); I2 (el orden de locks de SRE-01 se prueba por orden de inserción de movimientos en EF InMemory, no contra PostgreSQL); I3 (el test de rechazo usa `Contains("excede")`; el literal exacto del controller coincide con la spec — verificado estático); I4 (seam de identidad sólido); I5 (logs A5 dicen "txn manual" también para adelantos — cosmético).
- **Auditoría de tests ajustados (sin enmascaramiento)**: `CancellationPropagationTests` (preserva aserciones de propagación de token), `ControllerFactory` (param opcional, callers viejos intactos), helper `CashDrawerRateAnchorTests` (aditivo; los 7 tests A5 previos sin cambios), `CashDrawerClosureTests` (stub a firma nueva), `ClientHttpContractTests` (arnés reforzado con captura de body; tests existentes intactos). T3: ningún test existente modificado (una regresión anexada).

## Limitaciones

- RED de T1-T3 no re-verificable read-only (impl+tests en el mismo commit); GREEN reproducido exacto por el verificador (33/33, 111/111, 31/31) y conteos de suite consistentes (2155/2156/2162).
- E2E WPF gated no corrido (corre como Admin; sin regresión esperada).

## Artefactos / commits

Change `2026-10-09-audit-advance-invoice-hardening`; tracker `odd/tasks/auditoria-adelantos-facturas.md`; ANEXO 8.154. Commits: `0318fe3` (plan) + `d7616ac` (T1) + `68e9c6c` (T2) + `359e50f` (T3) + commit de cierre.
