# Fase inmediata de la auditoría integral re-emitida (SEC-05, SEC-06; SRE-04/SRE-05 refutados)

Objetivo: ejecutar la FASE INMEDIATA de la matriz de `docs/auditoria_integral_fases_1_4.txt` (re-emitida 2026-10-08): SEC-05 ([JsonIgnore] en User), SEC-06 (arqueo ciego: current-balance + clientes WPF) y las refutaciones SRE-04 (ya en 8.152) y SRE-05 (este change). Change: `openspec/changes/2026-10-09-audit-immediate/` (ANEXO 8.153). Branch: V0.15. Entrega: ask-on-risk → stacked-to-main (cacheada). RDD: off. Tests: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` · build `dotnet build CommandCenter.slnx -c Release` · cobertura `scripts/check-coverage.py`.

## Specs

- **S1 (SEC-05 — serialización segura de User)**: `PasswordHash` y `SecurityStamp` con `[System.Text.Json.Serialization.JsonIgnore]`; ningún JSON STJ expone valor ni nombre; test dedicado de serialización. Detalle: `specs/user-sensitive-serialization/spec.md`.
- **S2 (SEC-06 — arqueo ciego)**: (a) `GET /api/cashdrawer/current-balance` restringido a Admin,Manager (cashier → 403); (b) el rechazo del adelanto por efectivo insuficiente sin cifras (mensaje exacto "Saldo de efectivo en caja insuficiente para el monto solicitado."; extensión justificada — cierra el sondeo del saldo vía 409); (c) cliente WPF: `CanViewTheoreticalBalance` fail-closed; cajero no fetchea y ve "—"; diálogo de adelanto con `decimal? availableCashLocal` (tope local solo para supervisores; el servidor valida). Detalle: `specs/blind-current-balance/spec.md`.
- **S3 (refutaciones)**: SRE-05 = completa ya exige `Idempotency-Key` vía `IdempotencyRequestResolver` (400 sin clave / HIT en replay / 422 mismatch; tests en Phase1/Phase4/OutboxProcessor; cliente WPF envía la clave; el atributo `[RequireIdempotencyKey]` no existe en el repo). SRE-04 cerrado en 8.152. Sin cambio de código.

## Tasks

| ID | Specs | Route | Descripción | Estado / commit |
|----|-------|-------|-------------|-----------------|
| T1 | S1 | inline | SEC-05: JsonIgnore + test de serialización | pendiente |
| T2 | S2 | delegated (writer) | SEC-06 backend: authz del endpoint + mensaje enmascarado + tests | pendiente |
| T3 | S2 | delegated (writer) | SEC-06 WPF: cajero ciego en Caja/adelantos + diálogo nullable + tests | pendiente |
| T4 | S1-S3 | delegated (verify) + inline | Verificación independiente + refutación SRE-05 + suite/cobertura + ANEXO 8.153 + cierre | pendiente |

## Log

- L1 (2026-10-09) — Pedido del usuario, verbatim: "Procede con auditoria_integral_fases_1_4.txt" (tras el cierre de 8.152 y la oferta de planificar el roadmap restante por tramos).
- L2 (2026-10-09) — Verificación previa estática: SRE-04 ya refutado (8.152); **SRE-05 REFUTADO** (complete exige key vía resolver desde 'mandatory idempotency' 2c4be32; sin clave → 400; replay → HIT; 422 mismatch; tests Phase1/Phase4/OutboxProcessor; cliente WPF con clave en `SalesService.cs:406`; `[RequireIdempotencyKey]` no existe en el repo). **SEC-05 CONFIRMADO** (`User.cs:16,24` sin JsonIgnore). **SEC-06 CONFIRMADO** con vector adicional: el rechazo de adelanto filtra "Disponible: {saldo}" al cliente vía 409 de dominio (el guard server-side se conserva); el cliente WPF (`CashDrawerViewModel:223,412-413`) requiere adaptación (cajero ciego + diálogo nullable) porque el tope local bloquea con 0 y el 403 rompería el flujo con error genérico.
- L3 (2026-10-09) — Próximo: T1 inline (SEC-05), luego T2 (writer backend) y T3 (writer WPF).
