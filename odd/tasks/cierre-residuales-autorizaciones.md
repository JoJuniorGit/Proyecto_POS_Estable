# Cierre de Residuales del Hub de Autorizaciones (8.150 → 8.151)

Objetivo: cerrar TODOS los residuales registrados del feature 8.150 (ANEXO 8.150 §D + tracker `autorizaciones-remotas-signalr.md` L5-L16): gap NULL del índice de dedupe (D1/R3), TOCTOU de binding en consume (R1), clasificación sin re-lectura en expiración perezosa (R2), W3 del provider web (D3), ausencia de cancelación de solicitud (D4b), sin reconciliación de cola WPF (D4a), countdown por techo (D4c), sets own-resolved sin poda (D4d), paridad PUT (D5 — cierre por diseño con pin), WebSockets no ejercitados (D2), sin disparador WPF de precio manual (D6/G1), qualify del contrato 400 vs 403 (spec). E2 (E2E UI WPF) queda cancelado por decisión del mantenedor (WIP en `stash@{0}`) — no se revive. Branch: V0.15. SDD: `openspec/changes/2026-10-09-authorization-residual-closeout/`. RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` (con y sin `TEST_POSTGRES_CONNECTION`) · `npm test` + `npm run lint` (Web.Frontend) · build `dotnet build CommandCenter.slnx -c Release` · gated `tests/CommandCenter.Wpf.E2ETests`. ANEXO: 8.151.

## Specs

- **R1 — Dedupe NULL-safe (D1/R3)**: en PostgreSQL el índice único parcial de Pending se recrea como `NULLS NOT DISTINCT` (migración aditiva nueva; la original queda inmutable); SQLite conserva semántica NULL-distinct y la dedupe secuencial del servicio. Escenario: duplicado `(user, NULL, action)` Pending concurrente → 23505 → `Deduplicated`.
- **R2 — Consumo atómicamente bindeado (R1)**: `TryConsumeAsync` incluye acción/venta/hash de contexto en el WHERE del claim atómico; con 0 filas, una lectura de clasificación preserva los outcomes precisos. Sin pre-lectura TOCTOU.
- **R3 — Clasificación de expiración perezosa (R2)**: cuando el claim de expiración perezosa pierde (0 filas), se re-lee antes de clasificar; una aprobación concurrente nunca se reporta como `Expired`.
- **R4 — Cancelación del solicitante (D4b)**: `AuthorizationStatus.Cancelled` (terminal, sin migración) + `POST /api/authorizations/{id}/cancel` (solo el solicitante; atómico; auditoría con resolver nulo); push de cierre `AuthorizationResolved` (status Cancelled, SIN token) a solicitante + elevados; mensajes exactos de carrera/expirada al perder; no-solicitante rechazado.
- **R5 — Liquidación del wait web (D3/W3)**: el reducer liquida la promesa del caller en rechazo/expiración (`{ ok:false, outcome:'rejected'|'expired', reason }`) manteniendo el modal de acuse; rechazo y cancel manual distinguibles.
- **R6 — Reconciliación WPF en reconnect (D4a)**: evento `Reconnected` del hub service + re-sync de la cola de notificaciones vía `GetStatusAsync` (resolved/expired/cancelled).
- **R7 — Pulido de clientes (D4c/D4d)**: countdown por piso (web + WPF); poda de sets own-resolved; foco inicial en el diálogo de notificación WPF.
- **R8 — Disparador WPF "Precio manual" (D6/G1)**: agregar producto NORMAL con precio manual (USD/Bs; Bs derivado de la tasa si se omite) por el mismo `AddItemAsync` → cajero dispara el flujo protegido; elevado aplica directo.
- **R9 — WebSockets reales (D2)**: test gated en el proyecto WPF E2E: SignalR contra el backend real negocia transporte WebSockets + roundtrip create/resolve.
- **R10 — PUT por diseño (D5)**: test que fija el comportamiento deliberado del `PUT /{id}/items` (403 por roles sin extensiones de flujo, sin aceptar token); documentado como alcance.
- **R11 — Qualify del contrato de replay**: con token ya consumido y SIN `Idempotency-Key`, aplica primero el 400 de clave obligatoria (el 403 del contrato aplica con clave presente).
- **R12 — Cierre**: verificación final + ANEXO 8.151 + verify-report + tracker/estado.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| W1 | R1-R4, R11 | delegated (writer) + verify | Backend: migración NULLS NOT DISTINCT + dedupe; claim atómico bindeado; re-lectura de expiración perezosa; cancelación (enum+servicio+coordinator+controller+push) + escenario E1 de cancelación | pendiente |
| W2 | R5, R7 (web) | delegated (writer) + verify | Web: liquidación del wait (reducer+provider), cancel server best-effort, cierre `Cancelled` en notificaciones admin, countdown piso, poda own-resolved | pendiente |
| W3 | R6, R7 (WPF) | delegated (writer) + verify | WPF: `Reconnected` + reconciliación de cola, cancel server best-effort, `Cancelled` en notificaciones, countdown piso, poda, foco inicial | pendiente |
| W4 | R8 | delegated (writer) + verify | WPF "Precio manual" (diálogo USD/Bs + wiring en el flujo de sugerencias/PosViewModel) | pendiente |
| W5 | R9, R10, R12 | delegated (writer) + verify + inline | WebSockets gated E2E + pin del PUT + verificación final + ANEXO 8.151 + cierre | pendiente |

Estrategia de entrega: `ask-on-risk` → cadena **stacked-to-main** cacheada. Commits `fix(8.151)` por work unit.

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Haz cierre de todos los problemas reciduales".
- L2 (2026-10-09) — Plan creado: change `2026-10-09-authorization-residual-closeout` (proposal, design D1-D9, specs de cliente y backend, tasks W1-W5, state) + este tracker. Interpretación registrada: cerrar los residuales del 8.150 documentados en ANEXO 8.150 §D y tracker L5-L16; E2 permanece cancelado por decisión previa del mantenedor (stash preservado); el veto L6 (UI web de precio manual / gate de anulación) permanece vigente. Próximo: W1 (backend).
