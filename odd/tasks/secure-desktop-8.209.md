# HTTPS desktop, secretos de desarrollo e idempotencia de caja (8.209)

Objetivo: aplicar la combinación aprobada: HTTPS en conexiones remotas Desktop/Web, secretos de Desarrollo en User Secrets y claves idempotentes en apertura/cierre/staging con un corte de compatibilidad gradual.
Rama: `V0.15`. Verificación: `dotnet build CommandCenter.slnx -c Release`, suite .NET con PostgreSQL, `npm test`, `npm run lint`, `npm run build`.

## Specs

- **S1 — HTTPS remoto.** Con certificado disponible, HTTP remoto recibe 307 a HTTPS para cualquier plataforma; HTTP loopback se conserva para desarrollo/automatización. El desktop migra direcciones guardadas a HTTPS, fija certificados privados con DPAPI TOFU y sus HttpClient usan el callback común. Pairing no publica ni reclama QR remoto sin TLS.
- **S2 — Secretos Development.** Credenciales locales salen de `appsettings.Development.json` y pasan a User Secrets (`Backend.API`); el archivo local se elimina. `AdminSeedPassword` de bootstrap se rota en User Secrets. No se rota el rol compartido de PostgreSQL.
- **S3 — Idempotencia escalonada.** Desktop y Web envían `Idempotency-Key` estable por payload para caja open/close; Desktop lo envía para stage de facturas. El servidor registra la clave; el flag `Idempotency:RequireCashSessionAndInvoiceStage` permite pasar de compatibilidad (false) a obligatoriedad (true) después de actualizar toda la flota.
- **S4 — Verificación y documentación.** Evidencia .NET/Web completa; runbooks explican cert trust, User Secrets y cuándo activar el flag de enforcement.

## Tasks

| ID | Specs | Estado | Evidencia |
| --- | --- | --- | --- |
| WU1 | S2 | completado | `UserSecretsId`; secretos migrados; `appsettings.Development.json` ausente |
| WU2 | S1 | completado | redirect remoto + pinning común + defaults/migración Desktop HTTPS + pairing seguro |
| WU3 | S3 | completado | keys estables en WPF/Web; flag de enforcement preparado (false hasta actualizar la flota) |
| WU4 | S4 | completado | build 0/0; .NET 3614/3614 con PostgreSQL; web 626/626; lint/build OK |
| WU5 | S1 | completado | reverse proxy Caddy (TLS 8443) + Kestrel loopback + forwarded headers/pairing + runbook |

## Log

- **L1 (2026-10-26):** Inicio por aprobación del mantenedor. El seed de administrador se rotó en User Secrets; la contraseña del usuario existente no se modifica automáticamente. El password del rol `postgres` se conserva porque puede ser compartido con otras bases/proyectos.
- **L2 (2026-10-26, cierre):** HTTPS remoto con 307 y loopback preservado; desktop migrado a `https://localhost:5001/` con pinning DPAPI común y pairing sin QR sin TLS; secretos de Development movidos a User Secrets y archivo local eliminado; claves idempotentes estables en WPF/Web y flag de enforcement preparado. Evidencia: build 0/0; suite .NET **3613/3613** con PostgreSQL; web **626/626**, lint limpio y build OK; clúster detenido.
- **L3 (2026-10-26, reverse proxy):** D implementado con Caddy en `8443` y Kestrel en loopback cuando `ReverseProxy:Enabled=true`; `ForwardedHeaders` confía explícitamente en loopback; runbook `docs/REVERSE_PROXY.md`, scripts de exportación PEM y setup; Caddyfile de ejemplo. Evidencia actualizada: suite .NET **3614/3614** con PostgreSQL; web 626/626; clúster detenido.
