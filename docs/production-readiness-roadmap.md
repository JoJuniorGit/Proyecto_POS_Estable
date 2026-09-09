# Production Readiness Roadmap - Sistema POS CommandCenter

Documento vivo de seguimiento del plan de estabilizacion hacia produccion con
un cliente real. Registra fases, tareas, hitos, decisiones, riesgos aceptados y
el avance real de cada fase.

- **Version documento:** 0.1.0
- **Fecha creacion:** 2026-09-09
- **Estado general:** Fase 0 EN CURSO (definicion de alcance y criterios de salida)
- **Rama base:** V0.15
- **Responsables:** Release Manager (RM) / Arquitecto (ARQ) / Cliente (CLI)
- **Fuente de estado del sistema:** `Reporte de estado.txt` (v1.5.0) y `docs/reporte.txt` (ANEXOS)

---

## 1. Registro de avance (changelog)

| Fecha       | Fase | Hito/Actividad                              | Evidencia / Estado           |
|-------------|------|---------------------------------------------|------------------------------|
| 2026-09-09  | F0   | Registro formal de Req.1-4 y aprobaciones (3.7/3.8): 4 cajas, 4 metodos de pago, facturacion digital diferida (base), QA | Requerimientos Req.1-4 ABIERTOS; Req.2 y 5-8 aprobados |
| 2026-09-09  | F0   | Respuestas preliminares RM/ARQ a las 8 preguntas del alcance (3.5.1); setup.iss revertido | Documento v0.1.0; pendiente validar con CLI |
| 2026-09-09  | F0   | Creacion del roadmap y formalizacion Fase 0 | Documento v0.1.0; DQ-001 a DQ-005 definidas |

---

## 2. Tablero de fases

| Fase | Nombre                                   | Estado    | Hito de salida            | Fecha salida |
|------|------------------------------------------|-----------|---------------------------|--------------|
| F0   | Definicion de alcance y criterios        | EN CURSO  | M0: alcance/SLOs/riesgos firmados | -        |
| F1   | Auditoria de deuda tecnica y seguridad   | PENDIENTE | M1: cero P0 abiertos      | -            |
| F2   | Refactorizacion critica y endurecimiento | PENDIENTE | M2: revision arquitectonica aprobada | -       |
| F3   | QA, concurrencia y pruebas de estres     | PENDIENTE | M3: evidencia reproducible | -            |
| F4   | Release engineering y despliegue         | PENDIENTE | M4: RC firmado y probado   | -            |
| F5   | Observabilidad y operacion               | PENDIENTE | M5: soporte sin desarrollo | -            |
| F6   | Piloto controlado                        | PENDIENTE | M6: aceptacion del cliente | -            |
| FIN  | Certificacion Go/No-Go                   | PENDIENTE | Firmas RM/ARQ/CLI          | -            |

---

## 3. FASE 0 - Definicion de alcance y criterios de salida (EN CURSO)

Objetivo: fijar con el cliente que significa "listo para produccion" y congelar
la version candidata. Esta fase NO produce codigo: produce decisiones y el
contrato operativo del roadmap.

### 3.1 Alcance confirmado (linea base)

| Aspecto | Estado actual (linea base 2026-09-09) | Confirmacion cliente |
|---------|----------------------------------------|----------------------|
| Topologia | Una sucursal / un puesto (sin `BranchId`, intencion futura) | PENDIENTE |
| Cajas | 4 terminales simultaneas (Req. 1); WPF fija + Web tablet | PENDIENTE |
| Moneda | Doble: USD referencia + Bs.S a tasa BCV; ventas mixtas y vuelto cruzado | PENDIENTE |
| Metodos de pago | 4 habilitados: Efectivo, Tarjeta (Punto de Venta), Transferencia/Pago Movil, Divisas (USD) - Req. 2 (aprobado) | Aprobado (punto 2) |
| Impresion fiscal | NO implementada (limitacion pre-piloto, INSTALLATION seccion 9); facturacion digital diferida a base arquitectonica (Req. 3) | PENDIENTE |
| Facturacion digital | Diferida: solo base arquitectonica (interfaces/stubs) - Req. 3 | PENDIENTE |
| Operacion sin red | Tasa BCV manual documentada (INSTALLATION seccion 7) | PENDIENTE |
| Entorno | LAN (HTTP 5000 + HTTPS 5001 autofirmado por sitio) | PENDIENTE |
| Updater | NO instalado (sin firma X.509, 8.20-A03/8U-N2) | PENDIENTE |

### 3.2 Decisiones tomadas (registro DQ)

- **DQ-001 - Congelacion de version candidata.** La linea base de estabilizacion
  es la rama `V0.15` en el commit `4a5e360` (REVISION 8.31, 2026-09-09). Durante
  la estabilizacion NO se incorporan funcionalidades nuevas; solo correcciones
  aprobadas por el RM y registradas en este documento.
- **DQ-002 - Regla de congelacion de codigo.** Toda correccion en estabilizacion
  requiere: commit + suites verdes (.NET 738/738, Web 80/80, build Release 0/0,
  lint 0/0) + actualizacion de `Reporte de estado.txt` + ANEXO en
  `docs/reporte.txt`. (Politica vigente de AGENTS.md.)
- **DQ-003 - Impresion no bloquea Fase 0.** Se trata como decision de cliente en
  Fase 1 (P0 o limitacion aceptada). Si el cliente exige impresion fiscal,
  escala a bloqueante y reabre alcance.
- **DQ-004 - SLOs son PROPUESTA inicial** (seccion 3.3) sujeta a validacion del
  cliente y a medicion real en Fase 3. No se declaran cumplidos hasta tener
  evidencia.
- **DQ-005 - Metricas de aceptacion heredadas.** Umbrales vigentes del proyecto:
  cobertura de dominio Core >= 0.70, Sales.Module >= 0.80, Inventory.Module >=
  0.72 (8.26-E4); `TreatWarningsAsErrors` en Release.

### 3.3 SLOs y objetivos de recuperacion (propuesta a validar)

| Metrica | Propuesta | Como se mide / evidencia | Estado |
|---------|-----------|--------------------------|--------|
| Disponibilidad mensual | >= 99.5 % | Uptime del servicio en jornada del puesto | PENDIENTE medicion |
| RPO | <= 24 h | Backup diario 03:00 (tarea programada, 8.29-A6) | PENDIENTE validar real |
| RTO | <= 4 h | Restore documentado (INSTALLATION 8.1) + ejecucion real Fase 3 | PENDIENTE ejecutar |
| Checkout sin duplicados | 100 % ante reintentos | Idempotency-Key + tests de abonos/lote | PARCIAL (tests verdes) |
| Concurrencia de terminales | 4 cajas simultaneas sin conflictos de estado ni cuellos de botella de BD (Req. 1) | Auditoria de capacidad en F3 (xmin + transaccion compartida) | PENDIENTE certificar |
| API p95 latencia | < 500 ms (nominal) | Carga en Fase 3 | PENDIENTE medir |
| Checkout p95 | < 1 s (nominal) | Carga en Fase 3 | PENDIENTE medir |
| Tasa de error tecnico | < 1 % | Separar errores de negocio vs tecnicos (RFC 7807) | PENDIENTE medir |
| Perdida de datos | 0 ventas | Restore + conteos en Fase 3 | PENDIENTE probar |
| Recalculo historico | PROHIBIDO | rules.md seccion 1; snapshots inmutables | CUBIERTO por tests |

### 3.4 Matriz de riesgos conocidos (registro inicial)

Escala impacto: P0 bloqueante / P1 alto / P2 medio / P3 bajo. Se revisa en cada
fase; P0 y P1 deben cerrarse antes de la certificacion.

| ID | Riesgo | Impacto | Mitigacion / plan | Estado |
|----|--------|---------|-------------------|--------|
| R-001 | Instalador Inno Setup sin smoke E2E en maquina limpia | P1 | Fase 1: ejecucion real ISCC + checklist | ABIERTO |
| R-002 | Restore de PostgreSQL nunca ejecutado en entorno operativo | P1 | Fase 1/3: restore real + medicion RTO | ABIERTO |
| R-003 | Certificado HTTPS autofirmado; advertencia en cajas remotas | P2 | Distribuir `.cer` en raiz de confianza o HTTPS real; documentar (INSTALLATION seccion 9) | ABIERTO |
| R-004 | Puerto HTTP 5000 expuesto en LAN | P2 | Aceptar topologia LAN o forzar HTTPS (SecuritySettings:RequireHttpsMetadata) | ABIERTO |
| R-005 | Tasa BCV: fail-open historico documentado ante tasa indisponible; plan offline manual | P1 | Confirmar con cliente politica offline; pruebas en Fase 3 | ABIERTO |
| R-006 | Impresion de recibos/cierres no implementada | P2 | Confirmar expectativa del cliente (Fase 1, DQ-003) | ABIERTO |
| R-007 | UpdaterService sin firma X.509; no instalado | P3 | Mantener deshabilitado; roadmap tras piloto | ABIERTO |
| R-008 | Operacion multi-sucursal sin `BranchId` | P3 | Intencion futura; fuera de alcance del piloto (coding-guidelines seccion 5) | ABIERTO |
| R-009 | Smoke de restauracion/migracion depende de BD real del cliente | P1 | Provisionar entorno QA con PostgreSQL dedicado en Fase 1/3 | ABIERTO |
| R-010 | deuda WPF/CI/rendimiento (M01..M06, R09..R28, I01..I07...) | P2/P3 | Clasificar en Fase 1 (bloqueante/aceptable/post) | ABIERTO |

### 3.5 Preguntas abiertas al cliente (bloquean M0)

1. Numero de cajas y usuarios concurrentes por jornada.
2. Metodos de pago requeridos en el piloto.
3. Impresion de recibos/cierres: obligatoria u opcional en el piloto.
4. Operacion sin internet: politica de tasa manual aceptada?
5. Topologia de red: LAN sola, o se requiere HTTPS real/remoto.
6. Requisitos regulatorios/fiscales de facturacion y conservacion.
7. Ventana de mantenimiento y responsable operativo en el sitio.
8. Duracion y criterios de exito del piloto (1-2 semanas iniciales).

### 3.5.1 Respuestas preliminares (RM/ARQ) - para validar con el cliente

Respuestas basadas en el estado actual del codigo y la arquitectura. Cada una
queda como RECOMENDACION del RM/ARQ pendiente de confirmacion del cliente (CLI).

| # | Pregunta | Respuesta preliminar RM/ARQ (evidencia) | Necesita confirmacion CLI |
|---|----------|------------------------------------------|---------------------------|
| 1 | Cajas y usuarios concurrentes | Sin limite fijo por codigo (sucursal unica); la concurrencia critica es stock/ventas del mismo producto, protegida por `xmin`. Recomendacion: iniciar con 2 cajas y <= 5 usuarios; validar capacidad real en F3 (p95 checkout). | Numero real de cajas/usuario del cliente |
| 2 | Metodos de pago | Modelo `PaymentMethods` por catalogo configurable (Admin); efectivo fisico/digital (`IsPhysicalCash`) y vuelto cruzado USD/Bs.S. Recomendacion: efectivo (fisico+digital) + los adicionales que el cliente indique. | Catalogo de metodos del negocio |
| 3 | Impresion recibos/cierres | NO implementada; cierre como PDF (`ClosurePdfGenerator`) y consulta en pantalla. Recomendacion (DQ-003): en el piloto validar la factura/cierre en pantalla/PDF; si el cliente exige impresion termica/fiscal, escala a P0 en F1 (requiere desarrollo). | Obligatoria u opcional en el piloto |
| 4 | Operacion sin internet | Auto-sync configurable (`BcvSettings:AutoSyncIntervalMinutes`, default 120; <= 0 desactiva), tasa manual del dia por Admin (`POST /api/exchange-rate`, ceil 4 decimales) y rechazo explicito de venta sin tasa vigente. Existe un fail-open documentado ante tasa indisponible (usa tasa del cliente) a validar en F1/F3 (R-005). | Aceptacion de la politica manual y del fail-open |
| 5 | Topologia de red | LAN: HTTP 5000 (API/Web) + HTTPS 5001 autofirmado por sitio; `SecuritySettings:RequireHttpsMetadata` default false. Recomendacion: LAN aislada, HTTPS 5001 para dispositivos, distribuir `.cer` en la raiz de confianza de cada caja. Acceso remoto/otra VLAN requiere HTTPS real (CA) + ajuste de firewall. | Topologia real de la tienda |
| 6 | Requisitos regulatorios/fiscales | El sistema NO es facturador fiscal: sin impresion fiscal SNTT ni IVA en la logica de precio (modelo Tax-Free Cost+Margin). Facturacion legal obligatoria seria requisito NUEVO (P0) a agregar al alcance. | Si aplica facturacion legal/comprobantes |
| 7 | Ventana de mantenimiento / responsable | Servicio NSSM auto-start; backup diario 03:00 (tarea programada). Recomendacion: ventana nocturna fuera de horario de caja; responsable operativo con acceso al runbook. | Horario y responsable del sitio |
| 8 | Duracion y criterios de exito del piloto | Recomendacion: 1 sucursal, 1-2 semanas, una version congelada, sin perdida de datos ni incidentes criticos, operacion dentro de SLOs (3.3) y al menos un restore de validacion. | Duracion y criterios de exito del cliente |

### 3.6 Criterios de salida de Fase 0 (M0)

- [ ] Alcance (3.1) confirmado por el cliente (Preguntas 3.5 respondidas).
- [ ] SLOs (3.3) validados y firmados.
- [ ] Matriz de riesgos (3.4) revisada con el cliente.
- [ ] Version candidata congelada (DQ-001) y comunicada.
- [ ] Responsable operativo en el sitio designado.
- [ ] Go/No-Go de Fase 0 registrado por RM/ARQ/CLI.

### 3.7 Requerimientos del cliente (registro Req-XX)

Registro formal de los requerimientos especificos del cliente para el roadmap.
Cada uno lleva: definicion, alcance, aprobacion, fase de ejecucion y estado.

| ID | Requerimiento | Definicion / alcance | Aprobacion | Fase ejecucion | Estado |
|----|---------------|----------------------|------------|----------------|--------|
| Req.1 | Concurrencia de 4 cajas | Auditoria de capacidad: certificar mediante pruebas que la arquitectura y las medidas actuales de concurrencia (`xmin`, transaccion compartida Sales/Inventory) soportan 4 terminales simultaneas sin conflictos de estado ni cuellos de botella de BD | Aprobado | F3 (QA/estres) | ABIERTO |
| Req.2 | Metodos de pago | Habilitar 4 metodos de pago para el cierre de transacciones: Efectivo, Tarjeta (Punto de Venta), Transferencia / Pago Movil, Divisas (USD) | APROBADO (punto 2) | F2 (configuracion) | ABIERTO |
| Req.3 | Facturacion digital | Implementacion DIFERIDA: NO generar la factura aun. Objetivo actual: dejar preparada la base arquitectonica (interfaces/stubs) para implementarla sin fricciones en la siguiente fase; base preparada para impresion fisica asincrona (proceso en background estrictamente no bloqueante para el cajero) | Aprobado | F2 (base arquitectonica) | ABIERTO (solo base) |
| Req.4 | Garantia de confiabilidad (QA) | Someter el sistema a pruebas rigurosas de estres y manejo de fallos para validar plena confiabilidad antes de produccion | Aprobado | F3 (QA/estres) | ABIERTO |
| Req.5-8 | Resto del plan | Puntos 5, 6, 7 y 8 del plan general: correctos y aprobados para ejecucion sin modificaciones | APROBADOS | segun fase | ABIERTO |

### 3.8 Estado de aprobaciones del plan general

| Punto del plan | Estado |
|----------------|--------|
| 2 (Metodos de pago - Req.2) | APROBADO |
| 5, 6, 7, 8 | APROBADOS sin modificaciones |
| Resto | Pendiente de revision / segun fases |

---

## 4. FASE 1 - Auditoria de deuda tecnica y seguridad (PENDIENTE)

Hito M1: cero riesgos P0 abiertos y matriz de riesgos firmada.

### 4.1 Instalador y entorno (P0)
- [ ] Ejecutar instalador Inno Setup en maquina limpia.
- [ ] Verificar servicio NSSM, cuenta virtual, ACL, secrets.json, cert HTTPS,
      firewall y tarea de backup.
- [ ] Validar migracion, creacion de BD y seed admin con cambio de contrasena.
- [ ] Ejecutar backup y restore real en instancia PostgreSQL separada.
- [ ] Validar reinicio, cambio de contrasena y actualizacion del servicio.
- [ ] Resolver desajustes doc/scripts/layout instalado.
- [ ] Decidir formalmente impresion fiscal (bloqueante o limitacion).

### 4.2 Seguridad
- [ ] `dotnet list package --vulnerable`, `npm audit`, escaneo de secretos.
- [ ] Revision JWT/expiración/revocacion/lockout/MustChangePassword.
- [ ] Decision explicita sobre HTTP 5000 (LAN) o HTTPS forzado.
- [ ] Revision CORS, pairing QR, rate limiting, security headers, RBAC.
- [ ] Updater fuera del cliente hasta firma X.509.

### 4.3 Arquitectura
- [ ] Definir politica BCV offline (sin fail-open financiero no documentado).
- [ ] Revisar proyecciones DTO y N+1 (8.16-H06, etc.).
- [ ] Revisar coordinacion SalesDbContext/InventoryDbContext.
- [ ] Clasificar deuda WPF/CI en bloqueante / aceptable / post-produccion.

---

## 5. FASE 2 - Refactorizacion critica y endurecimiento (PENDIENTE)

Hito M2: revision arquitectonica aprobada; sin defectos P0/P1 de datos,
seguridad o disponibilidad.

- [ ] Instalador idempotente (re-ejecucion segura, upgrade sin perder secretos,
      rollback por etapa).
- [ ] Separacion config publica / secretos por sitio / artefactos dev.
- [ ] Fail-fast de configuracion al arranque con mensajes operativos.
- [ ] Completar proyecciones DTO en historial/caja/catalogo.
- [ ] Eliminar N+1; aplicar AsNoTracking/AsSplitQuery.
- [ ] Corregir UI bloqueante WPF; cancelacion cooperativa en operaciones largas.
- [ ] Dispose de timers/messenger/sockets/CancellationTokenSource.
- [ ] Auditoria de acciones administrativas y eventos de seguridad.
- [ ] Versionado de esquema y plan upgrade/rollback.

---

## 6. FASE 3 - QA, concurrencia y pruebas de estres (PENDIENTE)

Hito M3: pruebas funcionales, concurrencia, estres y restore con evidencia
reproducible.

### 6.1 Funcional
- [ ] Mantener suites: .NET 738/738, Web 80/80, lint 0, build Release 0/0.
- [ ] E2E sobre instalacion real (caja, mixto USD/Bs.S, vuelto, abonos,
      reintentos, cierre, reservas/entregas, cambio de tasa, roles).
- [ ] Errores RFC 7807 visibles en WPF y Web.

### 6.2 Concurrencia y fallos
- [ ] Ventas concurrentes sobre mismo producto / dos cajas descontando stock.
- [ ] Reintentos con misma Idempotency-Key.
- [ ] Corte de red en pagos/cierres/abonos; reinicio durante transaccion.
- [ ] Recuperacion de Outbox en estado Dispatching (reclaim stale).
- [ ] Cambio de tasa con ventas en espera; historial inmutable.

### 6.3 Rendimiento y recuperacion
- [ ] API p95 < 500 ms; checkout p95 < 1 s; error tecnico < 1 %.
- [ ] Prueba sostenida >= 8 h y pico a 2x usuarios esperados.
- [ ] Reinicio de PostgreSQL y backend.
- [ ] Restore real + conteos (ventas/pagos/tasas/cierres) + medicion RTO.

---

## 7. FASE 4 - Release engineering y despliegue (PENDIENTE)

Hito M4: Release Candidate instalable, firmado, reproducible y probado en
maquina limpia.

- [ ] Rama de release congelada; artefacto unico desde CI.
- [ ] Verificacion automatica: 0 secretos, 0 pfx dev, 0
      appsettings.Development.json, hashes, dependencias aprobadas.
- [ ] Compilacion ISCC real y prueba del instalador.
- [ ] Firmado de instalador/binarios.
- [ ] Checklist de instalacion por cliente; matriz config dev/QA/piloto/prod.
- [ ] Procedimiento de migracion/rollback y upgrade sin perder secrets.json.

---

## 8. FASE 5 - Observabilidad y operacion (PENDIENTE)

Hito M5: operacion monitorizada; soporte capaz de recuperar el sistema sin el
equipo de desarrollo.

- [ ] Logs estructurados con traceId/usuario/rol/SaleId/resultado (sin secretos).
- [ ] Separacion de logs app/seguridad/BD/backup/servicio.
- [ ] Health checks backend, PostgreSQL, migraciones, tasa BCV, disco, ultimo
      backup exitoso.
- [ ] Alertas: servicio caido, backup fallando, errores BD, concurrencia alta,
      disco bajo, cert por expirar.
- [ ] Retencion/exportacion de logs; runbooks (servicio caido, restore, cert,
      BCV, stock, rollback); responsable de soporte y ventana de mantenimiento.

---

## 9. FASE 6 - Piloto controlado (PENDIENTE)

Hito M6: piloto sin incidentes criticos, sin perdida de datos y dentro de SLOs.

- [ ] Desplegar en una sucursal; duracion inicial 1-2 semanas; version congelada.
- [ ] Registro diario: ventas, cierres, errores, latencia, backups,
      intervenciones.
- [ ] Restore en copia aislada durante el piloto.
- [ ] Revision diaria de incidencias; sin funcionalidades nuevas.
- [ ] Aceptacion formal del cliente.

---

## 10. Certificacion final (Go/No-Go)

El sistema se certifica listo para produccion cuando se cumplan SIMULTANEAMENTE:

1. Cero riesgos P0/P1 abiertos en la matriz (3.4).
2. Instalador probado en maquina limpia + reinstalacion verificada (F1/F4).
3. Migraciones, seed y actualizacion probados (F1/F4).
4. Backup y restore reales con RTO medido (F1/F3).
5. Concurrencia y estres superados con evidencia (F3).
6. Artefacto Release firmado y sin secretos (F4).
7. HTTPS/RBAC/proteccion de datos aprobados (F1/F2).
8. Politica BCV offline definida y probada (F1/F3).
9. Observabilidad, alertas y runbooks operativos (F5).
10. Piloto aceptado por el cliente (F6).
11. Responsable operativo y procedimiento de rollback definidos (F0/F5).
12. Go/No-Go firmado por RM, ARQ y CLI.

---

## 11. Protocolo de actualizacion de ESTE documento

- El changelog (seccion 1) es el registro de avance: nueva fila arriba con
  fecha, fase, actividad y evidencia.
- Toda correccion de codigo durante la estabilizacion actualiza ademas
  `Reporte de estado.txt` y registra ANEXO en `docs/reporte.txt` (DQ-002).
- El estado del tablero (seccion 2) y el detalle de la fase en curso se
  mantienen sincronizados con la realidad del repo; no se marca una fase cerrada
  sin su hito y su evidencia.
- Las decisiones se numeran DQ-### y los riesgos R-###; no se reutilizan IDs.
