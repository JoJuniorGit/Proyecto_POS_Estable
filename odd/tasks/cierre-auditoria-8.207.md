# Cierre de residuales de auditoría (8.207)

Objetivo: cerrar los 5 hallazgos abiertos y los residuales de mayor riesgo que la verificación 8.207 confirmó, en el orden: SEC-12, SRE-02a, SEC-15, SEC-14, PERF-R03/R01/13.
Rama: `V0.15`. Runner: `dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj` + carril PostgreSQL aislado.

## Specs

- **S1 — SEC-12:** si un método electrónico con esperado > 0 no fue declarado en el cierre, el cierre se rechaza con un mensaje accionable; no se auto-completa con el esperado.
- **S2 — SRE-02a:** la idempotencia de `AddItem` y del cierre diario se registra en la misma transacción que la mutación de negocio; un timeout no puede duplicar ítems ni cierres.
- **S3 — SEC-15:** `local-resolve` exige que el llamador sea el solicitante (o elevado) y rechaza supervisores con `MustChangePassword`.
- **S4 — SEC-14:** `sortBy=cost` y `sortBy=costprice` se normalizan y enmascaran para roles sin permiso de catálogo.
- **S5 — PERF-R03:** `ConfirmPickupAsync` elimina la carga completa preliminar del grafo y usa una proyección ligera.
- **S6 — PERF-R01:** la coincidencia de facturas proveedor resuelve los nombres en un solo lote, sin una consulta por nombre.
- **S7 — PERF-13:** los predicados de búsqueda usan `ILIKE` en PostgreSQL y conservan el fallback `ToLower` para InMemory.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S1 | completado | cierre rechaza electrónico activo omitido; focalizados 24/24 |
| WU2 | S2 | completado | AddItem y cierre con idempotencia en la misma transacción; 14/14 + test atómico |
| WU3 | S3 | completado | ownership + `MustChangePassword` en local-resolve; tests nuevos verdes |
| WU4 | S4 | completado | `costprice` normalizado a `name`; test de masking verde |
| WU5 | S5 | completado | proyección liviana en `ConfirmPickup`; 4/4 |
| WU6 | S6 | completado | batch `unnest` de similitud; test PostgreSQL 1/1 |
| WU7 | S7 | completado | `ILIKE` en PostgreSQL con fallback InMemory |
| WU8 | Verificación | completado | build 0/0; suite completa 3602/3602 con PostgreSQL |

## Log

- **L1 (2026-10-26):** inicio por pedido del mantenedor. La verificación previa confirmó 18 cerrados, 16 parciales y 5 abiertos; este tramo ataca los 5 abiertos y los residuales de mayor riesgo.
- **L2 (2026-10-26, cierre):** SEC-12 (rechazo de electrónico activo omitido; inactivo se expone con contado 0), SRE-02a (idempotencia atómica en `AddItem` y cierre diario), SEC-15 (ownership y `MustChangePassword` en local-resolve), SEC-14 (`costprice` enmascarado), PERF-R03 (proyección liviana + guard de custodia), PERF-R01 (batch `unnest` con GIN) y PERF-13 (`ILIKE` con fallback InMemory). Build 0/0; suite completa **3602/3602** con PostgreSQL aislado. El clúster se detuvo al finalizar.
