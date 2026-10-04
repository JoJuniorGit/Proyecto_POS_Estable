# Entregas Parciales (Mercancía en Custodia) — Retiros Pendientes

Objetivo: retiros fraccionados de pedidos pagados en custodia con trazabilidad por línea y log de auditoría por evento. Branch: V0.15. Delivery: ask-on-risk (chain pendiente de elección del usuario). SDD: `openspec/changes/2026-10-03-custody-partial-deliveries/`. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · `npm test` (Web.Frontend) · build `dotnet build CommandCenter.slnx -c Release`.

## Specs

Fuente: pedido del usuario, verbatim (L1). Estados y trazabilidad según el pedido.

- **S1 (Estados del Pedido)**: "La factura debe soportar tres estados logísticos: Pendiente (100% en tienda), Entrega Parcial (fraccionado) y Completado (0% en tienda)."
- **S2 (Trazabilidad de Líneas)**: "Cada producto dentro de la factura debe tener un desglose lógico de Cantidad Total, Cantidad Entregada y Cantidad Pendiente."
- **S3 (Historial de Auditoría)**: "Cada retiro debe generar un registro inmutable en la base de datos indicando: Fecha y hora del retiro, ID del Cajero que despachó, y detalle de las cantidades retiradas en ese evento."
- **S4 (Interfaz de Despacho)**: "Al hacer clic en 'Confirmar Retiro', en lugar de liquidar la factura de golpe, se debe abrir un modal o expandir una vista donde el cajero pueda ingresar manualmente la cantidad a retirar hoy para cada producto."
- **S5 (Validación de UI)**: "El input numérico de retiro no debe permitir ingresar una cantidad mayor a la Cantidad Pendiente."
- **S6 (Indicador Visual)**: "Los pedidos con estado Entrega Parcial deben tener un Badge visual distinto (ej. color azul o naranja) y mostrar una barra de progreso o texto resumen (ej. 'Retirado: 4/10 artículos')."
- **S7 (Comprobante de Despacho)**: "Al confirmar un retiro parcial, el sistema debe permitir imprimir un 'Ticket de Entrega' o 'Nota de Despacho' que sirva como comprobante de los ítems entregados en ese momento específico."
- **S8 (supuesto, no pedido literal)**: la feature aplica a Desktop (WPF) y Web (React) porque ambos clientes exponen Retiros Pendientes; verificar con el usuario si solo se quiere uno. Marcado como supuesto a confirmar.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1-S3 | delegated (writer) | Dominio + migración + backfill + smoke (PR1) | hecho — verificado PASS WITH WARNINGS (suite 1543/1543, build 0/0); commit 1f17988 |
| T2 | S1-S3 | delegated (writer) | Servicio `DeliverPartialAsync` + endpoint idempotente + tests (PR2) | hecho — verificado PASS WITH WARNINGS (suite 1563/1563, build 0/0); commit 106dcdc |
| T3 | S7 | delegated (writer) | Nota de Despacho PDF + endpoint (PR3) | hecho — verificado PASS (suite 1572/1572, build 0/0); commit c5d9ae7 |
| T4 | S4-S7 | delegated (writer) | UI WPF: modal, badge/progreso, impresión (PR4) | hecho — verificado PASS WITH WARNINGS (suite 1585/1585, build 0/0; fix wiring modal en T4c); commit pendiente |
| T5 | S4-S7 | delegated (writer) | UI Web: modal, badge/progreso, impresión (PR5) | pendiente |

Cadena elegida (2026-10-03): stacked-to-main (opción 1). Trabajo en V0.15; cada slice commitea a V0.15 y queda PR-able a main.

## Log

- L1 (2026-10-03) — Pedido del usuario, verbatim:

> Implementar flujo de Entregas Parciales (Mercancía en Custodia) en Retiros Pendientes
>
> Descripción:
> Refactorizar el módulo de "Retiros Pendientes" para permitir que los clientes realicen retiros fraccionados de un pedido pagado a lo largo de múltiples días. El sistema debe llevar trazabilidad exacta de qué artículos se entregaron, las cantidades pendientes y quién autorizó cada entrega parcial.
>
> Criterios de Aceptación (Backend y Base de Datos):
>
> Estados del Pedido: La factura debe soportar tres estados logísticos: Pendiente (100% en tienda), Entrega Parcial (fraccionado) y Completado (0% en tienda).
>
> Trazabilidad de Líneas: Cada producto dentro de la factura debe tener un desglose lógico de Cantidad Total, Cantidad Entregada y Cantidad Pendiente.
>
> Historial de Auditoría (Log): Cada retiro debe generar un registro inmutable en la base de datos indicando: Fecha y hora del retiro, ID del Cajero que despachó, y detalle de las cantidades retiradas en ese evento.
>
> Criterios de Aceptación (Frontend UI/UX):
> 4. Interfaz de Despacho: Al hacer clic en "Confirmar Retiro", en lugar de liquidar la factura de golpe, se debe abrir un modal o expandir una vista donde el cajero pueda ingresar manualmente la cantidad a retirar hoy para cada producto.
> 5. Validación de UI: El input numérico de retiro no debe permitir ingresar una cantidad mayor a la Cantidad Pendiente.
> 6. Indicador Visual: Los pedidos con estado Entrega Parcial deben tener un Badge visual distinto (ej. color azul o naranja) y mostrar una barra de progreso o texto resumen (ej. "Retirado: 4/10 artículos").
> 7. Comprobante de Despacho: Al confirmar un retiro parcial, el sistema debe permitir imprimir un "Ticket de Entrega" o "Nota de Despacho" que sirva como comprobante de los ítems entregados en ese momento específico.

- L2 (2026-10-03) — Evidencia de exploración (codegraph + explorer read-only): flujo actual `ConfirmPickupAsync` plano (`SalesService.History.cs:16-40`), endpoint `POST /api/sales/{id}/confirm-pickup` (`SalesController.Checkout.cs:193-210`), enum binario (`SaleDeliveryStatus.cs:3-7`), sin log de eventos, sin cantidad entregada por línea; RDD OFF (clone-local); openspec activo; ANEXO siguiente 8.145.
- L3 (2026-10-03) — Artefactos SDD creados: exploration, proposal, 4 specs, design, tasks, state.yaml (chain pendiente). Supuesto S8 (ambos frontends) pendiente de confirmación implícita del usuario al aprobar el plan.
- L4 (2026-10-03) — Usuario eligió estrategia de cadena: stacked-to-main (opción 1). El usuario no objetó el alcance de ambos frontends (S8 sigue como supuesto de implementación). Próximo: T1 (dominio + migración + backfill) delegado a writer, luego verificación independiente antes del commit del slice.
- L5 (2026-10-03) — T1 completado (commit 1f17988) y verificado independientemente: PASS WITH WARNINGS — suite completa 1543/1543, build Release 0/0, migración/backfill estáticamente correctos, xmin de SaleItem consistente con el precedente (AddXminConcurrencyTokensToSales: sin DDL, solo snapshot). Warnings: smoke Postgres gated no ejecutado localmente (TEST_POSTGRES_CONNECTION ausente); xmin en SaleItem afecta updates existentes pero sin cobertura Postgres local. Próximo: T2 (servicio DeliverPartialAsync + endpoint idempotente).
- L6 (2026-10-03) — T2 (T2a servicio + T2b endpoint + T2c corrección) verificado independientemente: PASS WITH WARNINGS — suite completa 1563/1563, build 0/0. Primera verificación FAIL: fixture preexistente `HoldNotClaimedPreconditionTests` (venta sin ítems) + divergencia de idempotencia (registro post-commit). Corrección autorizada (mismo patrón que el fixture ya aprobado por el usuario para HoldOrderClaimTests): fixture con ítem pendiente y aserciones de claim intactas; idempotencia movida DENTRO de la transacción del servicio (2 SaveChanges + commit, JSON con DeliveryId, null-safe sin hash), espejando CompleteSale/HoldOrders. Verificado: replay idéntico, mismatch 422, Driver 403, over-pending 400, 409 legacy. Warnings: semántica xmin/unique real solo verificable con Postgres; sin test de rename de usuario. Commit del slice: 106dcdc.
- L7 (2026-10-03) — T3 (Nota de Despacho PDF + endpoint de descarga) verificado independientemente: PASS (8/8 checks) — suite completa 1572/1572, build 0/0. Generador PDF directo (Helvetica/WinAnsi, sin librería externa) con hora local Venezuela, cajero, cliente, ítems y pendientes históricos reconstruidos al momento del evento (`Id <= deliveryId`; reimpresión estable, test 6 vs 3); aviso "NO ES UNA FACTURA FISCAL"; no recibe datos monetarios (no recalcula). Endpoint sale-scoped con Driver 403, 404 ProblemDetails, application/pdf. Commit del slice: c5d9ae7.
- L8 (2026-10-03) — T4 (UI WPF: modal de cantidades, badge azul + progreso "Retirado: X/Y", impresión de nota, restauración de borrador tras 409) verificado: primera ronda FAIL por 2 defectos de wiring WPF (IsCancel con Command no cierra; Click dispara antes que Command → 2 activaciones) + suite sin totales por timeout del invocador. Corrección T4c (solo wiring XAML/code-behind: 1 IsCancel, comando explícito en handler) re-verificada: PASS WITH WARNINGS — suite 1585/1585 (comando estable `-c Release --no-build`), build 0/0, wiring estático correcto. Warnings: sin test interactivo del modal; E2E WPF de salud no ejecutado. Commit del slice: pendiente.
