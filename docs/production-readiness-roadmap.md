# Production Readiness Roadmap - Sistema POS CommandCenter

Documento vivo de seguimiento del plan de estabilizacion hacia produccion con
un cliente real. Registra fases, tareas, hitos, decisiones, riesgos aceptados y
el avance real de cada fase. Es la capa ejecutiva/operativa del plan de
certificacion; el detalle por revision vive en `docs/reporte.txt` (ANEXOS) y el
estado tecnico en `Reporte de estado.txt`.

- **Version documento:** 0.2.0 (fusion con el plan de certificacion consolidado)
- **Fecha:** 2026-09-09
- **Estado general:** Fases F0-F6 en curso; bloquean la certificacion 5 brechas operativas
- **Rama base:** V0.15
- **Responsables:** Release Manager (RM) / Arquitecto (ARQ) / Cliente (CLI)
- **Fuente de estado del sistema:** `Reporte de estado.txt` (v1.24.0) y `docs/reporte.txt` (ANEXOS)

---

## 1. Resumen ejecutivo y estado actual

| Dimensión | Estado actual |
|---|---|
| Arquitectura | 9 proyectos .NET 10 + React 19/Vite 8; modular (Core → Sales/Inventory → API → Desktop/Web) |
| Tests .NET | **766/766** pasan (xUnit + Moq + WebApplicationFactory + PostgreSQL real) |
| Tests Web | **80/80** pasan (node:test) + oxlint 0 errores |
| Build | Release 0 errores / 0 warnings (`TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest`) |
| Cobertura | Core ≥ 0.70, Sales.Module ≥ 0.80, Inventory.Module ≥ 0.72 (gate CI) |
| CI | GitHub Actions: build + test + cobertura + `npm audit` + `dotnet list package --vulnerable` |
| Migraciones | 61 clases de migracion EF Core (32 Sales + 29 Inventory); `MigrateAsync` fail-fast al arranque |
| Seguridad | JWT HMAC-SHA256, RBAC, RFC 7807, secrets.json con ACL, SecurityHeaders, rate limiting |
| Instalador | Inno Setup + NSSM + `Configure-PosService.ps1` (logica idempotente) |

### 1.1 Trabajo ya completado (fases avanzadas)

- Seguridad auditada (F1): JWT, CORS, RBAC, pairing, rate limiting, headers, secrets, npm audit.
- Aislamiento DTO (F2): PaymentMethods, Products, CashDrawer.GetHistory.
- Concurrencia 4 cajas (F3/Req.1): xmin + transaccion compartida certificada vs PostgreSQL real.
- Idempotencia (F3/Req.4): Idempotency-Key + SHA-256 + tests de concurrencia + Outbox recovery.
- Metodos de pago (F2/Req.2): 4 metodos seed idempotente.
- Facturacion digital (F2/Req.3): PDF asincrono no bloqueante + endpoint on-demand + UI de cajero.
- Release artifact (F4): 0 secretos, 0 pfx dev, 0 appsettings.Development.
- Observabilidad (F5): logs estructurados, health checks, metricas de latencia/error, runbooks.
- Inmutabilidad del historial (F3): tests vs PostgreSQL real (rules.md §1).
- Stock negativo configurable (8.56) e importacion de variantes (8.57).

### 1.2 Brechas criticas para produccion

> Las 5 brechas bloquean la certificacion. Ninguna requiere desarrollo de
> funcionalidades nuevas: todas son verificacion, pruebas operativas y configuracion.

| # | Brecha | Esfuerzo estimado | Bloquea |
|---|--------|-------------------|---------|
| 1 | Instalador nunca probado E2E en maquina limpia | 1-2 dias | M1, M4 |
| 2 | Restore de PostgreSQL nunca ejecutado con RTO medido | 1 dia | M1, M3 |
| 3 | Prueba de estres sostenida (>= 8h, 4 terminales) sin ejecutar | 2-3 dias | M3 |
| 4 | Firma de codigo del instalador/binarios pendiente | 1 dia (con cert) | M4 |
| 5 | Alertas de monitoreo externo no configuradas | 1-2 dias | M5 |

Estimacion total hasta M6: 3-4 semanas (incluyendo 2 semanas de piloto).

---

## 2. Registro de avance (changelog)

| Fecha       | Fase | Hito/Actividad                              | Evidencia / Estado           |
|-------------|------|---------------------------------------------|------------------------------|
| 2026-09-09  | F5   | Health check de frescura del ultimo backup (8.59) | Suites .NET 769/769 |
| 2026-09-09  | F2   | Logistics.Module fuera del DI (M08) + dotnet-ef 10.0.4 (B02) + bundle web reproducible (8.58) | Suites .NET 766/766 |
| 2026-09-09  | -    | Fusion del roadmap con el plan de certificacion (v0.2.0) | Documento consolidado; contenido nuevo + formato existente |
| 2026-09-09  | F2   | Stock negativo configurable (8.56) + import de variantes (8.57) | Suites .NET 766/766 |
| 2026-09-09  | F2/F4/F5 | Metricas de latencia/error (8.52), cancelacion del cobro (8.53), scaffolding ISCC/firma (8.54) | Items 1-3 del plan preparados en codigo; suites .NET 761/761 |
| 2026-09-09  | F2   | Req.3 UI: endpoint recibo on-demand + integracion en modal de venta completada (279372d) | Req.3 IMPLEMENTADO; suites .NET 759/759 |
| 2026-09-09  | F2   | Req.3 factura digital NO fiscal + impresion asincrona NO bloqueante (e07644b) | Req.3 IMPLEMENTADO; suites .NET 757/757 |
| 2026-09-09  | F2/F3/F4/F5 | Cobertura desde codigo: DTO CashDrawer (8.46), health details (8.47), outbox multi-worker (8.48), instalador health post-arranque (8.49) | Suites .NET 755/755 |
| 2026-09-09  | F5   | Runbooks operativos en INSTALLATION 13 + baseline de observabilidad verificada (8.44) | F5 avanzado; alertas/monitoreo externo pendiente |
| 2026-09-09  | F4   | Artefacto Release reproducido y verificado; matriz config + checklist por cliente en INSTALLATION 11/12 (8.43) | F4 avanzado; ISCC/firma pendiente |
| 2026-09-09  | F3   | Inmutabilidad del historial vs PostgreSQL real (58ac014) | F3 avanzado; suites .NET 754/754 |
| 2026-09-09  | F2   | H06: aislamiento Entidad->DTO en PaymentMethodsController (7035b96) y N+1/DTO/plan esquema (8.40) | F2 checklist parcial completo |
| 2026-09-09  | F1   | Auditoria de seguridad: deps 0 vuln + pila JWT/CORS/rate-limit/RBAC verificada (8.37-8.38) | F1 checklist 4.2 seguridad COMPLETA |
| 2026-09-09  | F3   | Req.4 ampliado: idempotencia concurrente vs PostgreSQL real (4e4eae7) | Req.4 PARCIAL ampliado; suites .NET 753/753 |
| 2026-09-09  | F3   | Req.1 implementado: auditoria de concurrencia 4 cajas vs PostgreSQL real (fd63b76) | Req.1 IMPLEMENTADO; suites .NET 746/746 |
| 2026-09-09  | F2   | Req.2 implementado: seed idempotente de los 4 metodos de pago estandar (f68a40b) | Req.2 IMPLEMENTADO; suites .NET 744/744 |
| 2026-09-09  | F0   | Registro formal de Req.1-4 y respuestas RM/ARQ (3.7/3.5.1) | Requerimientos registrados; Req.2 y 5-8 aprobados |

---

## 3. Tablero de fases

| Fase | Nombre                                   | Estado    | Hito de salida            | Fecha salida |
|------|------------------------------------------|-----------|---------------------------|--------------|
| F0   | Definicion de alcance y criterios        | EN CURSO  | M0: alcance/SLOs/riesgos firmados | -        |
| F1   | Auditoria de deuda tecnica y seguridad   | EN CURSO | M1: cero P0 abiertos      | -            |
| F2   | Refactorizacion critica y endurecimiento | EN CURSO | M2: revision arquitectonica aprobada | -       |
| F3   | QA, concurrencia y pruebas de estres     | EN CURSO | M3: evidencia reproducible | -            |
| F4   | Release engineering y despliegue         | EN CURSO | M4: RC firmado y probado   | -            |
| F5   | Observabilidad y operacion               | EN CURSO | M5: soporte sin desarrollo | -            |
| F6   | Piloto controlado                        | EN CURSO | M6: aceptacion del cliente | -            |
| FIN  | Certificacion Go/No-Go                   | PENDIENTE | Firmas RM/ARQ/CLI          | -            |

---

## 4. FASE 0 - Definicion de alcance y criterios de salida

Objetivo: fijar con el cliente que significa "listo para produccion" y congelar
la version candidata. Esta fase NO produce codigo: produce decisiones y el
contrato operativo del roadmap.

### 4.1 Alcance confirmado (linea base)

| Aspecto | Estado actual | Confirmacion cliente |
|---------|---------------|----------------------|
| Topologia | Una sucursal / un puesto (sin `BranchId`, intencion futura) | PENDIENTE |
| Cajas | 4 terminales simultaneas (Req. 1); WPF fija + Web tablet | PENDIENTE |
| Moneda | Doble: USD referencia + Bs.S a tasa BCV; ventas mixtas y vuelto cruzado | PENDIENTE |
| Metodos de pago | 4 habilitados: Efectivo, Tarjeta (Punto de Venta), Transferencia/Pago Movil, Divisas (USD) | Aprobado (punto 2) |
| Impresion fiscal | NO implementada (limitacion pre-piloto, INSTALLATION 9) | PENDIENTE |
| Facturacion digital | IMPLEMENTADA: recibo/nota de entrega NO fiscal PDF asincrono + endpoint on-demand (Req. 3) | PENDIENTE |
| Operacion sin red | Tasa BCV manual documentada (INSTALLATION 7) | PENDIENTE |
| Entorno | LAN (HTTP 5000 + HTTPS 5001 autofirmado por sitio) | PENDIENTE |
| Updater | NO instalado (sin firma X.509, 8.20-A03/8U-N2) | PENDIENTE |

### 4.2 Decisiones tomadas (registro DQ)

- **DQ-001 - Congelacion de version candidata.** Linea base: rama `V0.15`. Durante
  la estabilizacion NO se incorporan funcionalidades nuevas; solo correcciones
  aprobadas por el RM.
- **DQ-002 - Regla de congelacion de codigo.** Toda correccion requiere: commit +
  suites verdes + actualizacion de `Reporte de estado.txt` + ANEXO en
  `docs/reporte.txt`. (Politica vigente de AGENTS.md.)
- **DQ-003 - Impresion no bloquea el alcance base.** Se trata como decision de
  cliente en Fase 1; si el cliente exige impresion termica/fiscal, escala a P0.
- **DQ-004 - SLOs son PROPUESTA inicial** (4.3) sujeta a validacion del cliente y
  a medicion real en Fase 3. No se declaran cumplidos hasta tener evidencia.
- **DQ-005 - Metricas de aceptacion heredadas.** Cobertura de dominio Core >= 0.70,
  Sales.Module >= 0.80, Inventory.Module >= 0.72 (8.26-E4); `TreatWarningsAsErrors`.
- **DQ-006 - Topologia HTTP/HTTPS (F1, 8.38).** Piloto LAN aislada con HTTP 5000 +
  HTTPS 5001 autofirmado; `SecuritySettings:RequireHttpsMetadata` default false.
  Fuera de LAN se exige `true` + cert de CA.

### 4.3 SLOs y objetivos de recuperacion (propuesta a validar)

| Metrica | Propuesta | Como se mide / evidencia | Estado |
|---------|-----------|--------------------------|--------|
| Disponibilidad mensual | >= 99.5 % | Uptime del servicio en jornada | PENDIENTE medicion |
| RPO | <= 24 h | Backup diario 03:00 (tarea programada, 8.29-A6) | PENDIENTE validar real |
| RTO | <= 4 h | Restore documentado (INSTALLATION 8.1) + ejecucion real F3 | PENDIENTE ejecutar |
| Checkout sin duplicados | 100 % ante reintentos | Idempotency-Key + tests de abonos/lote | PARCIAL (tests verdes) |
| Concurrencia de terminales | 4 cajas simultaneas sin conflictos (Req. 1) | Auditoria en F3 (xmin + tx compartida) | CERTIFICADO (8.34) |
| API p95 latencia | < 500 ms (nominal) | `/api/health/requests` bajo carga | PENDIENTE medir |
| Checkout p95 | < 1 s (nominal) | Medicion E2E con 4 terminales | PENDIENTE medir |
| Tasa de error tecnico | < 1 % | Separar errores de negocio vs tecnicos (RFC 7807) | PENDIENTE medir |
| Perdida de datos | 0 ventas | Restore + conteos en F3 | PENDIENTE probar |
| Recalculo historico | PROHIBIDO | rules.md seccion 1; snapshots inmutables | CUBIERTO por tests |

### 4.4 Matriz de riesgos conocidos

Escala impacto: P0 bloqueante / P1 alto / P2 medio / P3 bajo. P0 y P1 deben
cerrarse antes de la certificacion.

| ID | Riesgo | Impacto | Mitigacion / plan | Estado |
|----|--------|---------|-------------------|--------|
| R-001 | Instalador Inno Setup sin smoke E2E en maquina limpia | P1 | F1: ejecucion real ISCC + checklist | ABIERTO |
| R-002 | Restore de PostgreSQL nunca ejecutado en entorno operativo | P1 | F1/F3: restore real + medicion RTO | ABIERTO |
| R-003 | Certificado HTTPS autofirmado; advertencia en cajas remotas | P2 | Distribuir `.cer` en raiz de confianza o HTTPS real | ABIERTO |
| R-004 | Puerto HTTP 5000 expuesto en LAN | P2 | Aceptar topologia LAN o forzar HTTPS (RequireHttpsMetadata) | ABIERTO |
| R-005 | Tasa BCV: fail-open documentado ante tasa indisponible; plan offline manual | P1 | Confirmar politica offline con cliente; pruebas en F3 | ABIERTO |
| R-006 | Impresion de recibos/cierres no implementada (termica) | P2 | Confirmar expectativa del cliente (F1, DQ-003) | ABIERTO |
| R-007 | UpdaterService sin firma X.509; no instalado | P3 | Mantener deshabilitado; roadmap tras piloto | ABIERTO |
| R-008 | Operacion multi-sucursal sin `BranchId` | P3 | Intencion futura; fuera de alcance (coding-guidelines 5) | ABIERTO |
| R-009 | Smoke de restauracion/migracion depende de BD real del cliente | P1 | Provisionar entorno QA con PostgreSQL dedicado en F1/F3 | ABIERTO |
| R-010 | deuda WPF/CI/rendimiento (M01..M06, R09..R28, I01..I07...) | P2/P3 | Clasificada en F1 (mayoria post-produccion) | PARCIAL |

### 4.5 Preguntas abiertas al cliente (bloquean M0)

1. Numero de cajas y usuarios concurrentes por jornada.
2. Metodos de pago requeridos en el piloto.
3. Impresion de recibos/cierres: obligatoria u opcional en el piloto.
4. Operacion sin internet: politica de tasa manual aceptada?
5. Topologia de red: LAN sola, o HTTPS real/remoto.
6. Requisitos regulatorios/fiscales de facturacion y conservacion.
7. Ventana de mantenimiento y responsable operativo en el sitio.
8. Duracion y criterios de exito del piloto (1-2 semanas iniciales).

### 4.5.1 Respuestas preliminares (RM/ARQ) - para validar con el cliente

| # | Pregunta | Respuesta preliminar RM/ARQ | Necesita confirmacion CLI |
|---|----------|-----------------------------|---------------------------|
| 1 | Cajas/usuario | Sin limite fijo; concurrencia critica por `xmin`. Recomendacion: iniciar con 2 cajas y <= 5 usuarios; validar p95 en F3. | Numero real del cliente |
| 2 | Metodos de pago | Catalogo configurable (Admin); efectivo fisico/digital + vuelto cruzado. Recomendacion: efectivo + los que el cliente indique. | Catalogo del negocio |
| 3 | Impresion | Recibo/nota de entrega NO fiscal en PDF (implementado 8.50-8.51); impresion termica no integrada. Si se exige termica/fiscal, escala a P0. | Obligatoria u opcional |
| 4 | Offline BCV | Auto-sync configurable; tasa manual por Admin; rechazo sin tasa vigente; fail-open documentado (R-005). | Aceptar politica manual |
| 5 | Topologia | LAN HTTP+HTTPS autofirmado (DQ-006); remoto/otra VLAN exige HTTPS real. | Topologia real |
| 6 | Regulatorio/fiscal | NO es facturador fiscal (sin SNTT/IVA). Facturacion legal seria P0 nuevo. | Si aplica |
| 7 | Mantenimiento/responsable | NSSM auto-start; backup 03:00; ventana nocturna; responsable con runbook. | Horario/responsable |
| 8 | Piloto | 1 sucursal, 1-2 semanas, version congelada, dentro de SLOs, >= 1 restore de validacion. | Duracion/criterios |

### 4.6 Criterios de salida de Fase 0 (M0)

- [ ] Alcance (4.1) confirmado por el cliente (Preguntas 4.5 respondidas).
- [ ] SLOs (4.3) validados y firmados.
- [ ] Matriz de riesgos (4.4) revisada con el cliente.
- [ ] Version candidata congelada (DQ-001) y comunicada.
- [ ] Responsable operativo en el sitio designado.
- [ ] Go/No-Go de Fase 0 registrado por RM/ARQ/CLI.

### 4.7 Requerimientos del cliente (registro Req-XX)

| ID | Requerimiento | Definicion / alcance | Aprobacion | Fase | Estado |
|----|---------------|----------------------|------------|------|--------|
| Req.1 | Concurrencia de 4 cajas | Certificar por pruebas que la concurrencia actual soporta 4 terminales sin conflictos | Aprobado | F3 | IMPLEMENTADO (8.34) |
| Req.2 | Metodos de pago | Habilitar 4 metodos: Efectivo, Tarjeta (PDV), Transferencia/Pago Movil, Divisas (USD) | APROBADO (punto 2) | F2 | IMPLEMENTADO (8.33) |
| Req.3 | Facturacion digital | Recibo/nota de entrega NO fiscal PDF asincrono + endpoint on-demand + UI de cajero | Aprobado | F2 | IMPLEMENTADO (8.51); impresora termica futura |
| Req.4 | Garantia de confiabilidad (QA) | Pruebas de estres y manejo de fallos antes de produccion | Aprobado | F3 | PARCIAL (8.36 fallos/idempotencia); estres end-to-end pendiente |
| Req.5-8 | Resto del plan | Puntos 5, 6, 7 y 8 del plan general aprobados sin modificaciones | APROBADOS | segun fase | ABIERTO |

---

## 5. FASE 1 - Auditoria de deuda tecnica y seguridad

Hito M1: cero riesgos P0 abiertos y matriz de riesgos firmada.

### 5.1 Instalador y entorno (P0 - bloqueante)

| Tarea | Riesgo ref. | Estado |
|-------|-------------|--------|
| Ejecutar instalador Inno Setup en maquina limpia (sin .NET ni PostgreSQL) | R-001 | PENDIENTE |
| Verificar servicio NSSM, cuenta virtual, ACL, secrets.json, cert HTTPS, firewall, tarea backup | R-001 | PENDIENTE |
| Validar migracion, creacion de BD y seed admin con cambio de contrasena obligatorio | R-001 | PENDIENTE |
| Ejecutar backup + restore real en instancia PostgreSQL separada; medir RTO | R-002, R-009 | PENDIENTE |
| Verificar reinicio del servicio, actualizacion de binarios y rollback de esquema | R-002 | PENDIENTE |

> CAUTION: R-001 y R-002 son P1 activos que deben cerrarse antes de cualquier
> despliegue. El instalador nunca ha sido probado E2E en maquina limpia y el
> restore nunca se ejecuto en entorno operativo.

### 5.2 Seguridad (completada)

- [x] Vulnerabilidades de dependencias: .NET 0 vuln, npm 0 vuln (8.37).
- [x] JWT/expiraci on/revocacion/lockout/MustChangePassword verificados (8.38).
- [x] CORS, pairing QR, rate limiting, security headers, RBAC verificados (8.38).
- [x] Decision DQ-006: HTTP/HTTPS en LAN documentada.
- [x] Escaneo de secretos sin fugas.
- [x] Updater fuera del cliente hasta firma X.509.

### 5.3 Arquitectura - decisiones pendientes

| Decision | Impacto | Accion |
|----------|---------|--------|
| Politica BCV offline formal | R-005 (P1) | Confirmar con cliente: tasa manual + rechazo sin tasa |
| Impresion de recibos/cierres | R-006 (P2) | Confirmar con cliente: obligatoria u opcional (DQ-003) |
| UpdaterService | R-007 (P3) | Mantener deshabilitado; no incluir en instalador |
| Multi-sucursal | R-008 (P3) | Fuera de alcance; documentado |

### 5.4 Clasificacion de deuda tecnica

| Categoria | Items | Clasificacion |
|-----------|-------|---------------|
| WPF acoplamiento/rendimiento (M01-M05, R09-R28) | ~20 | Post-produccion (no bloquea operacion en caja) |
| CI/Tests (M06, B03, CI runner) | ~5 | Post-produccion (no afecta al cliente) |
| Logistics.Module en DI (M08) | 1 | Fase 2 - desregistrar del DI de produccion |
| UpdaterService sin firma (A03) | 1 | Post-produccion - mantener deshabilitado |
| dotnet-ef 10.0.3 -> 10.0.4 (B02) | 1 | Fase 2 - actualizacion trivial |
| Advisory lock fragil en retry (H16) | 1 | Aceptable - riesgo bajo en single-node |

---

## 6. FASE 2 - Refactorizacion critica y endurecimiento

Hito M2: revision arquitectonica aprobada; sin defectos P0/P1 de datos,
seguridad o disponibilidad.

- [x] Completar proyecciones DTO en historial/caja/catalogo (PaymentMethods 8.39; productos 8.40; CashDrawer.GetHistory 8.46).
- [x] Eliminar N+1; aplicar AsNoTracking/AsSplitQuery (verificado 8.40).
- [x] Dispose de timers/messenger/sockets/CancellationTokenSource (verificado 8.41).
- [x] Auditoria de acciones administrativas y eventos de seguridad (verificado 8.41).
- [x] Versionado de esquema y plan upgrade/rollback (INSTALLATION 10, 8.40).
- [x] Separacion config publica / secretos por sitio / artefactos dev (previa en 8.29).
- [x] Fail-fast de configuracion al arranque (previo).
- [x] Desregistrar `Logistics.Module` del DI en produccion (M08, 8.58).
- [x] Actualizar dotnet-ef a 10.0.4 (B02, 8.58) - alineado con EF Core 10.0.4.
- [x] Regenerar el bundle web `wwwroot` (reproducible, sin cambios de hash - 8.58).
- [ ] Corregir UI bloqueante WPF; cancelacion cooperativa en operaciones largas (parcial 8.53: checkout; general pendiente).
- [ ] Validar edge cases del stock negativo con el cliente (8.56).

---

## 7. FASE 3 - QA, concurrencia y pruebas de estres

Hito M3: pruebas funcionales, concurrencia, estres y restore con evidencia
reproducible.

### 7.1 Funcional (E2E en instalacion real)

| Escenario | Cobertura | Pendiente |
|-----------|-----------|-----------|
| Venta mixta USD/Bs.S con vuelto cruzado | Tests unitarios | E2E en instalacion real |
| Abonos parciales + reintentos | Tests 6/6 | E2E con Idempotency-Key |
| Cierre de caja + DailyClosure | Tests unitarios | E2E con totales verificados |
| Cambio de tasa BCV con ventas en espera | Tests + PostgreSQL real | E2E con 2+ terminales |
| Roles Admin/Cashier: RBAC funcional | Tests | E2E verificando restricciones |
| Reservas de stock + expiracion | Tests unitarios | E2E verificando stock post-expiracion |
| Errores RFC 7807 en WPF y Web | Tests | Verificacion visual |

### 7.2 Concurrencia y fallos

| Escenario | Estado |
|-----------|--------|
| Ventas concurrentes 4 cajas sobre mismo producto | CERTIFICADO (8.34) |
| Reintentos con misma Idempotency-Key | CERTIFICADO (8.36) |
| Recuperacion Outbox en estado Dispatching | CERTIFICADO (8.36) |
| Cambio de tasa con ventas en espera / historial inmutable | CERTIFICADO (8.42) |
| Drenaje Outbox multi-worker | CERTIFICADO (8.48) |
| Corte de red en pagos/cierres/abonos | PENDIENTE - simular desconexion durante checkout |
| Reinicio de PostgreSQL durante operacion | PENDIENTE - verificar reconexion y retry |
| Reinicio del backend durante transaccion | PENDIENTE - verificar consistencia post-crash |

### 7.3 Rendimiento y SLOs

| SLO | Objetivo | Como medir | Estado |
|-----|----------|------------|--------|
| API p95 latencia | < 500 ms | `/api/health/requests` (8.52) bajo carga | PENDIENTE |
| Checkout p95 | < 1 s | Medicion E2E con 4 terminales | PENDIENTE |
| Error tecnico | < 1 % | Separar 4xx negocio vs 5xx tecnico | PENDIENTE |
| Prueba sostenida | >= 8 h continuas | Operacion simulada con scripts | PENDIENTE |
| Pico de carga | 2x usuarios esperados | 8 terminales simultaneas (stress) | PENDIENTE |
| Disponibilidad | >= 99.5 % mensual | Uptime en jornada | PENDIENTE medicion real |

### 7.4 Recuperacion y datos

| Prueba | Objetivo | Estado |
|--------|----------|--------|
| Backup PostgreSQL (`pg_dump -Fc`) | RPO <= 24 h | Tarea programada configurada; ejecucion real PENDIENTE |
| Restore + conteos | 0 ventas perdidas | PENDIENTE |
| Medicion RTO | <= 4 h | PENDIENTE |
| Rollback de migracion | Esquema anterior funcional | Plan documentado (INSTALLATION 10); ejecucion PENDIENTE |

---

## 8. FASE 4 - Release engineering y despliegue

Hito M4: Release Candidate instalable, firmado, reproducible y probado en
maquina limpia.

- [x] Rama de release congelada; artefacto unico desde build-release.ps1.
- [x] Verificacion automatica: 0 secretos, 0 pfx dev, 0 appsettings.Development.json (8.43).
- [x] Checklist de instalacion por cliente; matriz config dev/QA/piloto/prod (INSTALLATION 11/12, 8.43).
- [x] Procedimiento de migracion/rollback y upgrade sin perder secrets.json (INSTALLATION 10, 8.40).
- [x] Scaffolding de instalador Inno Setup y firma en build-release.ps1 (8.54).
- [ ] Compilacion ISCC real del instalador (requiere Inno Setup).
- [ ] Firmado de instalador/binarios con X.509 (requiere certificado).
- [ ] Smoke test en maquina virgen.
- [ ] Regenerar el bundle web en `wwwroot` (`npm run build` final) y verificarlo en el artefacto.

### 8.1 Matriz de configuracion por ambiente

| Setting | Development | QA | Piloto | Produccion |
|---------|-------------|----|--------|-----------|
| `ASPNETCORE_ENVIRONMENT` | Development | Production | Production | Production |
| `ConnectionStrings` | `.env` / appsettings.Dev | secrets.json | secrets.json | secrets.json |
| `SecuritySettings:RequireHttpsMetadata` | false | false | false (LAN) | true (remoto) |
| `BcvSettings:AutoSyncIntervalMinutes` | 120 | 120 | 120 | 120 |
| Certificado HTTPS | Efimero | Por sitio | Por sitio | CA confiable |
| Backup | Manual | Manual | Programado | Programado |

---

## 9. FASE 5 - Observabilidad y operacion

Hito M5: operacion monitorizada; soporte capaz de recuperar el sistema sin el
equipo de desarrollo.

- [x] Logs estructurados con traceId/usuario/rol/SaleId (sin secretos).
- [x] Separacion de logs: app/seguridad/BD/backup/servicio.
- [x] Health checks: `/health` + `/api/health/metrics` + `/api/health/requests` (8.52).
- [x] Health checks avanzados: migraciones / tasa BCV / disco / expiracion de cert (`/api/health/details`, 8.47).
- [x] Health check de frescura del ultimo backup (8.59, `/api/health/details`).
- [x] Metricas de latencia/error por peticion (8.52).
- [x] Runbooks en INSTALLATION 13: servicio caido, restore, cert, BCV, stock, rollback.
- [ ] Alertas externas: servicio caido, backup fallando, errores BD, disco bajo (requiere monitoreo externo).
- [ ] Cert por expirar: alerta proactiva (solo si HTTPS real).

> Recomendacion de monitoreo minimo para el piloto: un script PowerShell
> programado que consulte `/health` cada 5 min y notifique si falla 3 veces
> consecutivas. Para produccion completa: Prometheus + Grafana o SaaS.

---

## 10. FASE 6 - Piloto controlado

Hito M6: piloto sin incidentes criticos, sin perdida de datos y dentro de SLOs.

### 10.1 Plan del piloto

1. Desplegar en UNA sucursal; duracion inicial 1-2 semanas; version congelada.
2. Dias 1-3: operacion supervisada; dias 4-14: operacion autonoma con monitoreo.
3. Sin funcionalidades nuevas durante el piloto.
4. Ejecutar al menos UN restore de validacion en copia aislada y confirmar conteos.
5. Verificar diariamente el backup y `/health`.
6. Confirmar operacion offline de la tasa BCV si el puesto queda sin red.
7. Aceptacion formal del cliente (firma Go/No-Go de Fase 6).

### 10.2 Criterios de aceptacion del piloto

| Criterio | Umbral | Como se verifica |
|----------|--------|------------------|
| Perdida de datos | 0 ventas | Conteo BD vs recibos fisicos |
| Incidentes P0 | 0 | Registro diario de incidencias |
| Duplicacion de ventas | 0 | Query `GROUP BY Idempotency-Key HAVING COUNT > 1` |
| Disponibilidad | >= 99.5 % en jornada | Health check monitoring |
| Restore exitoso | 1 ejecucion durante el piloto | Restore en copia aislada + conteos |
| Backups | 100 % de ejecuciones programadas | Log de tarea Windows |
| Aceptacion del operador | Positiva | Encuesta/entrevista |

### 10.3 Registro diario durante el piloto

```
Fecha: _______________
Ventas del dia: ___ | Cierres: ___ | Errores: ___
Backup ejecutado: Si / No  | Latencia percibida: Normal / Lenta
Intervenciones: ____________________________________________
Observaciones: _____________________________________________
```

---

## 11. Certificacion final (Go/No-Go)

El sistema se certifica listo para produccion cuando se cumplan SIMULTANEAMENTE:

| # | Criterio | Fase | Estado |
|---|----------|------|--------|
| 1 | Cero riesgos P0/P1 abiertos en la matriz | F1 | PENDIENTE |
| 2 | Instalador probado en maquina limpia + reinstalacion | F1/F4 | PENDIENTE |
| 3 | Migraciones, seed y actualizacion probados | F1/F4 | PARCIAL |
| 4 | Backup y restore reales con RTO medido | F1/F3 | PENDIENTE |
| 5 | Concurrencia y estres con evidencia | F3 | PARCIAL (4 cajas OK; sostenida pendiente) |
| 6 | Artefacto Release firmado y sin secretos | F4 | PARCIAL (sin secretos OK; sin firma) |
| 7 | HTTPS/RBAC/proteccion de datos aprobados | F1/F2 | COMPLETO |
| 8 | Politica BCV offline definida y probada | F1/F3 | PARCIAL (definida OK; probada pendiente) |
| 9 | Observabilidad, alertas y runbooks | F5 | PARCIAL (runbooks OK; alertas pendientes) |
| 10 | Piloto aceptado por el cliente | F6 | PENDIENTE |
| 11 | Responsable operativo y rollback definidos | F0/F5 | PARCIAL |
| 12 | Go/No-Go firmado por RM, ARQ y CLI | FIN | PENDIENTE |

```
Release Manager:  _________________________ Fecha: ___________
Arquitecto:       _________________________ Fecha: ___________
Cliente:          _________________________ Fecha: ___________
```

---

## 12. Open Questions / Dependencias

1. **Rama / consolidacion:** el roadmap consolidado es ahora la capa ejecutiva; el
   detalle por revision queda en `docs/reporte.txt`. Confirmar que no se requieren
   dos documentos separados.
2. **Maquina limpia (sin .NET/PostgreSQL) para el instalador:** bloquea F1 y F4.
3. **Certificado de firma X.509:** sin el, el firmado (criterio 6) queda como
   riesgo aceptado para el piloto.
4. **Herramienta de monitoreo/alertas para el piloto:** script PowerShell simple
   (minimo viable), UptimeRobot (SaaS) o Prometheus+Grafana (on-premise).
5. **Clasificacion de deuda WPF como post-produccion:** confirmar para priorizar
   instalador y pruebas operativas sobre refactoring cosmetico de la UI.

---

## 13. Protocolo de actualizacion de ESTE documento

- El changelog (seccion 2) es el registro de avance: nueva fila arriba con
  fecha, fase, actividad y evidencia.
- Toda correccion de codigo durante la estabilizacion actualiza ademas
  `Reporte de estado.txt` y registra ANEXO en `docs/reporte.txt` (DQ-002).
- El estado del tablero (seccion 3) y el detalle de la fase en curso se
  mantienen sincronizados con la realidad del repo; no se marca una fase cerrada
  sin su hito y su evidencia.
- Las decisiones se numeran DQ-### y los riesgos R-###; no se reutilizan IDs.
- Las brechas criticas (seccion 1.2) se revisan en cada hito; una brecha se
  elimina solo con evidencia de que se cerro.