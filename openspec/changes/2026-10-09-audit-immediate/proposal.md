# Proposal: Fase inmediata de la auditoría integral (SEC-05, SEC-06; SRE-04/SRE-05 refutados) — ANEXO 8.153

## Contexto

La auditoría re-emitida `docs/auditoria_integral_fases_1_4.txt` (2026-10-08) define en su matriz una "FASE INMEDIATA — SPRINT PRIORITARIO" con 4 ítems: SEC-05, SEC-06, SRE-04 y SRE-05. El mantenedor autorizó ejecutar el roadmap restante por tramos ("Procede con auditoria_integral_fases_1_4.txt").

Estado de partida (verificación estática de este plan):

| Hallazgo | Veredicto | Evidencia |
|---|---|---|
| SRE-04 | REFUTADO en 8.152 | `InventoryDbContext.cs:122-131` + migraciones + `ProductConcurrencyTokenTests`; sin cambio de código (ver ANEXO 8.152). |
| SRE-05 (= parte abierta de SRE-02) | REFUTADO (este change) | `POST /{id}/complete` exige `Idempotency-Key` vía `IdempotencyRequestResolver` desde el change "mandatory idempotency" (2c4be32): sin clave → 400 (`IdempotencyRequestResolver.cs:47-51` + mensaje exacto en `SalesController.Checkout.cs:114-119`); replay → `X-Cache-Lookup: HIT` con la respuesta original (`:69-80`); payload distinto → 422 (`:82-87`). Tests: `Phase1SecurityRemediationTests.CompleteSale_WithoutIdempotencyKey_Returns400BadRequest`, `Phase4SharedTransactionAndIdempotencyTests.CompleteSale_WithIdempotencyKey_ReturnsMissOnFirstAndHitOnReplay`, `OutboxProcessorJobTests.CompleteSaleAsync_WithIdempotencyKey_ReturnsExistingInvoice_WhenAlreadyCompleted`. Cliente WPF envía la clave (`SalesService.cs:406` + `CheckoutViewModel._currentIdempotencyKey`). El atributo `[RequireIdempotencyKey]` que cita la auditoría NO existe en el repo (grep 0); el patrón real de 8.149 es el resolver compartido — el endpoint ya cumple el objetivo. |
| SEC-05 | CONFIRMADO | `Core/Entities/User.cs:16,24` sin `[JsonIgnore]`; `ServiceCollectionExtensions` serializa con STJ (sin Newtonsoft en el repo). |
| SEC-06 | CONFIRMADO (+ vector adicional) | `CashDrawerController.cs:126-132` permite Cashier en `current-balance`. Vectores: (a) endpoint; (b) el rechazo por efectivo insuficiente del adelanto devuelve `"…Disponible: {saldo}…"` al cliente (409 vía middleware, `InvalidOperationException` de dominio) — un cajero puede sondear el saldo con un adelanto excesivo, anulando el propósito del arqueo ciego. El guard server-side del adelanto (`CashAdvanceCoordinator.cs:57-63`) queda intacto. |

## Alcance

1. **S1 (SEC-05)**: `[System.Text.Json.Serialization.JsonIgnore]` en `PasswordHash` y `SecurityStamp` + tests de serialización.
2. **S2 (SEC-06)**:
   - Backend: `current-balance` restringido a `Admin,Manager`; el rechazo de adelanto por efectivo insuficiente NO incluye cifras de saldo.
   - Cliente WPF: el cajero deja de ver el efectivo esperado (representación neutra "—"), deja de invocar el endpoint, y el diálogo de adelanto opera sin tope local (el servidor valida) — el tope local se conserva para supervisores.
3. **S3 (refutaciones)**: SRE-05 documentado con evidencia (SRE-04 ya cerrado en 8.152); sin cambio de código.

Fuera de alcance: los 17 abiertos ya conocidos (se ejecutarán por tramos), el residual de céntimos de SEC-01, la ampliación de SEC-07 al AuthorizationHub (tramo propio) y cualquier refactor no exigido.

Nota de alcance en S2: el enmascaramiento del mensaje del adelanto (REQ-BCB-02) excede la letra del hallazgo (que solo pide restringir el endpoint) pero es necesario para el propósito del arqueo ciego; se registra explícitamente como extensión justificada y es reversible de forma independiente.

## Enfoque S2 (cliente)

- `CashDrawerViewModel.CanViewTheoreticalBalance` = Admin || Manager; **fail-closed** sin sesión (consistente con `IsAdmin`).
- `LoadSessionAsync`: con `CanViewTheoreticalBalance=false` no invoca `GetCurrentBalanceLocalAsync`; `FormattedBalanceBsS="—"`, `FormattedBalanceUsd=string.Empty`. Totales de ingresos/egresos e historial intactos.
- `ProcessCashAdvanceAsync`: con `false` no invoca el fetch y pasa `null` al diálogo.
- `IDialogService.ShowCashAdvanceRegisterDialogAsync(paymentMethods, decimal? availableCashLocal)`; `CashAdvanceRegisterViewModel.AvailableCashLocal` nullable: tope y mensaje de tope solo con valor; `AvailableCashDisplay` con formato N0 o "—"; sin tope local el botón confirma y el rechazo viene del servidor (mensaje enmascarado).
- Impacto esperado: los tests que construían la VM sin sesión y asertaban el saldo se ajustan con una sesión Admin (se reportan nombradamente).

## Tramos siguientes (indicativos, a autorizar/confirmar por el mantenedor)

- 8.154: adelantos/seguridad — SEC-03 (contrato + clientes) + SEC-04 (tasa anclada) + SRE-01 (facturas: orden por `ResolvedProductId`).
- 8.155: proveedores/performance — PERF-01 (batch staging) + PERF-02 (apply; choca con el retry xmin deliberado → decisión de diseño).
- 8.156: WPF/perf + clientes — PERF-04, PERF-05, CLEAN-05.
- 8.157: SignalR — SEC-07 (ForceDisconnect + IHubFilter sobre AuthorizationHub/ExchangeRateHub).
- 8.158: observabilidad/bootstrap — SRE-03 + CLEAN-03.
- 8.159: refactors — CLEAN-01 (93 [NonAction]) + CLEAN-04 + PERF-03.

## Entrega y riesgos

- Estrategia: `ask-on-risk` → cadena `stacked-to-main` (cacheada). Forecast ~450-550 líneas (S2 toca backend + WPF + varios tests ajustados).
- Riesgo principal: el cambio de UX del cajero (ya no ve el efectivo esperado) es la consecuencia directa del arqueo ciego; el tope del adelanto pasa a ser server-side. Si el mantenedor prefiere otra UX, es un veto reversible sobre el cliente.
- RDD: off (clone-local) → verificación = self-verification de writers + verificador independiente + suite/cobertura.
