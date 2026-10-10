# Proposal: Limpieza de controladores (CLEAN-01) + extracción del cálculo de cobro (CLEAN-04) + PERF-03 — ANEXO 8.159

## Contexto

Tramo 6 autorizado ("Procede con 8.159"; el tramo 8.158 — SRE-03/CLEAN-03 — queda pendiente). Alcance: **CLEAN-04** (fuga del motor de cálculo en el controlador), **CLEAN-01** (93 métodos zombis `[NonAction]`) y **PERF-03** (AsSplitQuery/AsNoTracking).

Verificación previa (estática, de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| CLEAN-04 | CONFIRMADO | `SalesController.Checkout.cs:20-91`: ~55 líneas de matemática financiera en el endpoint (conversiones, umbrales 0.05/0.01, redondeo, vuelto). El anclaje SEC-08 (8.152) llama `_salesService.ResolveCheckoutRateAsync` y debe preservarse. Nota: el `if (sale == null)` del controller es inalcanzable — `GetSaleAsync` lanza `KeyNotFoundException("Sale {id} not found.")` → 404 por middleware (comportamiento real a preservar). |
| CLEAN-01 | CONFIRMADO | **93** `[NonAction]` en 19 archivos; **~245 usos en tests** (con falsos positivos de reflexión `.GetMethod(` y mocks de servicios, a triage). `CancellationPropagationTests:251-266` pinea 7 wrappers por nombre (a migrar a los `*Async`). Los wrappers son passthroughs `public Task<...> X(args) => XAsync(args);`. |
| PERF-03 | **REFUTADO (ya remediado)** | `GetSaleEntityAsync` YA tiene `.AsSplitQuery()` (L146-147) y el parámetro `asNoTracking` (L149-152, usado por `GetSaleAsync` con `asNoTracking: true`); `AsSplitQuery` presente desde `148e6a3` ("fase 2 integridad… split query"). `History.cs` YA tiene `AsNoTracking` en todas las lecturas (L51, L73, L119, L130, L203) y `AsSplitQuery` donde hay colecciones. La auditoría re-emitida volvió a citar código viejo. Residual mínimo: el primer fetch de `ConfirmPickupAsync` (L19-25) es solo-lectura (validación + pendingItems) y no lleva `AsNoTracking` — se agrega como higiene (S3). |

## Alcance

1. **S1 (CLEAN-04)**: nuevo `Sales.Module/Interfaces/ICheckoutCalculationService.cs` + `Sales.Module/Services/CheckoutCalculationService.cs` que encapsula fetch + resolución de tasa + matemática; el endpoint queda auth + delegación + `Ok`. Registro DI scoped. Parámetro opcional en el ctor de `SalesController` (precedente 8.149/8.150) para no romper construcciones directas; los tests de preview construyen el servicio REAL con su mock de `ISalesService` (cobertura preservada end-to-end). Comportamiento EXACTO: missing → `KeyNotFoundException` (404 middleware, mensaje actual); `rate <= 0` → `ArgumentException("Tasa de cambio inválida.")` (400 por dominio); rechazo ±100% y anclaje SEC-08 intactos.
2. **S2 (CLEAN-01)**: eliminar los 93 wrappers `[NonAction]` (dos grupos: A=37 Auth/Users/PaymentMethods/Settings/ExchangeRate/Health/Receipts/Reservations; B=56 CashDrawer/DailyClosure/Products×3/Sales×7) y migrar TODOS los call sites de tests a los métodos `*Async` (token `CancellationToken.None`/default); `CancellationPropagationTests` migra su lista a los async. Cero cambios de contrato HTTP ni de lógica de negocio.
3. **S3 (PERF-03)**: refutación documentada con evidencia + residual `AsNoTracking` en el primer fetch de `ConfirmPickupAsync` (solo lectura).

Fuera de alcance: 8.158 (SRE-03/CLEAN-03), y cualquier refactor no exigido.

## Enfoque y decisiones

- D1 (CLEAN-04): la matemática pasa TAL CUAL (mismos umbrales/fórmulas); los errores se mantienen vía excepciones de dominio (`ArgumentException` → 400) y el 404 actual por `KeyNotFoundException` (el ApiNotFound inalcanzable del controller desaparece con la delegación — comportamiento observable idéntico).
- D2 (CLEAN-01): los wrappers se eliminan por grupo completo con su grupo de tests; el RED es compile-level (divulgado); ningún nombre `*Async` cambia.
- D3 (PERF-03): refutado como SRE-04/SRE-05 (la auditoría cita código pre-148e6a3); el residual es 1 línea segura.

## Entrega y riesgos

- Estrategia: `ask-on-risk` → `stacked-to-main` (cacheada). Forecast ~700-1000 líneas (churn mecánico de tests). Presupuesto de PR: candidato a cadena por slices (CLEAN-04 / CLEAN-01A / CLEAN-01B / cierre).
- Riesgo principal: CLEAN-01 toca ~40+ archivos de tests — mitigado con grupos acotados, compilación dirigida y suite completa; cualquier call site fuera de grupos se reporta.
- RDD: off (clone-local) → verificación independiente + suite/cobertura.

## Tramos siguientes

- 8.158 pendiente: SRE-03 (logger asíncrono) + CLEAN-03 (bootstrap fail-fast). Cierra el roadmap de la auditoría re-emitida.
