# Guía de Lanzamiento con Monitoreo Externo — Sistema POS CommandCenter

Guía operativa para poner el sistema en produccion junto con monitoreo externo
(alertas). Complementa a `INSTALLATION.md` (despliegue) y al
`production-readiness-roadmap.md` (fases).

---

## 1. Requisitos previos

- **PostgreSQL 16/18** como servicio, con usuario con `CREATEDB` y `CREATEROLE` (para el seed).
- **.NET SDK 10** para el build. El runtime del servicio es **self-contained** (no requiere .NET en el puesto).
- Maquina objetivo: **Windows** (el instalador/`Configure-PosService.ps1` y NSSM son Windows).
- Para monitoreo externo: servidor con **Prometheus + Grafana**, cuenta **SaaS** (UptimeRobot), o una PC que ejecute el script `monitor-health.ps1`.

## 2. Build y publish

```powershell
# Desde la raiz del repo (Windows):
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

Genera `publish\` con `BackendAPI`, `DesktopClient` y `UpdaterService`. El script hace
**scrub de secretos** (aborta si hay `*.pfx` o literales) y, si se configuran
`ISCC_PATH`/`SIGNTOOL_PATH`/`CODE_SIGNING_PFX`, compila y firma el instalador (opcional).

Para desarrollo rapido, el backend corre con `launchSettings.json` (Development, 5000/5001).

## 3. Configuracion de produccion

**Secretos** en `BackendAPI\secrets.json` (NO versionados; el instalador los genera por sitio con ACL):

```jsonc
{
  "ConnectionStrings": { "DefaultConnection": "Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=..." },
  "SystemSettings": { "AdminSeedPassword": "<clave-fuerte>" },
  "JwtSettings": { "Key": "<clave aleatoria >= 32 chars>" },
  "Kestrel": { "Certificates": { "Default": { "Password": "<pass del pfx>" } } }
}
```

**Variables de entorno / appsettings (produccion):**

| Clave | Valor |
|-------|-------|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `SecuritySettings:RequireHttpsMetadata` | `false` (LAN) / `true` (remoto) |
| `BcvSettings:AutoSyncIntervalMinutes` | `120` (o `0` si offline) |
| `Backup:Directory` | `C:\Backups\CommandCenter` (default) |
| `Backup:MaxAgeHours` | `24` (default) |

**Certificado HTTPS:** generado por sitio (`scripts/create-https-cert.ps1` / el instalador).
**Backup:** tarea programada diaria 03:00 (`scripts/backup-postgres.ps1`), default ON.

## 4. Instalacion como servicio (produccion)

El instalador Inno Setup (o `Configure-PosService.ps1`) registra el servicio NSSM
**`Sistema POS Backend`** con Virtual Account + ACL, firewall TCP 5000/5001,
`secrets.json`, certificado y tarea de backup. Al arrancar verifica `/health` automaticamente.

Verificacion manual:

```powershell
Invoke-RestMethod "http://localhost:5000/health"   # status=Healthy, database=Connected
Get-Service "Sistema POS Backend"                   # Running
schtasks /Query /TN "Sistema POS - Backup PostgreSQL"
```

## 5. Endpoints de monitoreo

| Endpoint | Auth | Que reporta |
|----------|------|-------------|
| `GET /health` | Anonimo | backend + PostgreSQL (`status`, `database`) |
| `GET /api/health/details` | Admin/Manager | migraciones, tasa BCV del dia, disco, expiracion de cert, **ultimo backup** |
| `GET /api/health/metrics` | Admin/Manager | metricas de cache |
| `GET /api/health/requests` | Admin/Manager | latencia (p95/avg/max) y errores 5xx por endpoint |

Token: `POST /api/auth/login` -> `token` (Bearer).

## 6. Monitoreo externo

### 6.a Minimo viable para el piloto — `monitor-health.ps1`

Script PowerShell que consulta `/health` (y opcionalmente `/api/health/details` para la
frescura del backup) y notifica si falla N veces consecutivas. Tiene dos modos:

- **Headless** (por defecto, para el Programador de tareas): una muestra por corrida, agrega
  CSVs de disponibilidad y p95 (`DataDir`), y con `-Summarize` escribe `slo-summary.json`.
- **Dashboard (`-Dashboard`)**: ventana WinForms con el estado en vivo, la frescura del
  backup, el resumen SLO vs metas, la gráfica de tendencia y el log. Modo de solo lectura:
  no agrega muestras ni alerta. Se abre desde el acceso directo "Monitor de Salud" del
  puesto instalado (o `.\docs\monitor-health.ps1 -Dashboard` en desarrollo).

Los valores por defecto pueden vivir en un archivo `monitor-config.json` (`-Config`);
los parametros de linea de comandos tienen precedencia. Ver `docs\monitor-config.json.example`.

El **modo muestreador** (`-SamplerSeconds N`, opcionalmente `-SamplerStopFile`/
`-SamplerMaxSamples`, `-ImportOnly`) agrega muestras periodicas a un `DataDir` de una corrida
concreta sin notificar; lo usa `scripts\pos-test.ps1` (runbook INSTALLATION.md §13.13) para
correlacionar disponibilidad y p95 de la sonda con la carga generada. `-ImportOnly` carga la
configuracion y settings compartidos sin ejecutar la sonda (dot-source limpio).

```powershell
# Ejemplo: tarea cada 5 min (o dashboard)
powershell -ExecutionPolicy Bypass -File "C:\Program Files (x86)\Sistema POS Administrador\tools\monitoring\monitor-health.ps1" `
    -Config "C:\ProgramData\CommandCenterPOS\monitoring\monitor-config.json" `
    -FailsToAlert 3
# Vista interactiva
powershell -ExecutionPolicy Bypass -File "...\tools\monitoring\monitor-health.ps1" -Dashboard
```

> **Nota sobre RPO:** el monitor alerta de backup stale a las **26 h** (`1560` min en el
> script), no a 24 h. El margen extra evita falsos positivos cuando el backup tarda
> mas de lo esperado en completarse (carga alta, reinicio post-cierre). El SLO formal
> sigue siendo RPO <= 24 h (roadmap §4.3); el valor de 26 h es el **umbral de alerta**
> del monitor, no la meta.

> **Nota:** el backend expone metricas en **JSON** (`/api/health/requests`), no en formato
> Prometheus. Para Prometheus+Grafana usar **blackbox_exporter** para sondear `/health`
> (y alertar por edad de backup via `/api/health/details`); para SLOs de latencia nativos
> convendria añadir `prometheus-net` (mejora futura).

### 6.b Prometheus + Grafana (produccion completa)

1. **blackbox_exporter** -> target `http://localhost:5000/health` (probe HTTP 200) y `https://localhost:5001`.
2. Reglas de alerta (Alertmanager):
   - `instance_down`: probe HTTP != 200 durante 3 min.
   - `backup_stale`: `/api/health/details` -> `lastBackupFresh == false`.
   - `disk_low`: `diskFreeMb` < umbral.
   - `cert_expiring`: `certExpiryUtc` < +30 dias.
3. Dashboard Grafana: paneles de `/health`, `/api/health/details` y `/api/health/requests` (p95 checkout, errores 5xx).

### 6.c SaaS (UptimeRobot / StatusCake)

- Monitor HTTP(S) -> `http://localhost:5000/health`, keyword `"Healthy"`, intervalo 5 min,
  alerta tras 3 fallos. Suficiente para disponibilidad del puesto; sin p95 de latencia
  (para eso el script o Prometheus).

## 7. Checklist de lanzamiento

- [ ] `build-release.ps1` OK (0 secretos, 0 pfx, sin `appsettings.Development.json`).
- [ ] Instalador/servicio NSSM corriendo; `/health` -> Healthy.
- [ ] `secrets.json` por sitio con ACL; cambio obligatorio de contrasena del admin.
- [ ] Backup programado ON; `lastBackupFresh == true` en `/api/health/details`.
- [ ] Monitoreo del piloto activo (script o Prometheus) con alertas de servicio caido y backup stale.
- [ ] Firewall 5000/5001 (subred local); decision HTTP/HTTPS (DQ-006).
- [ ] Cajas: importar `.cer` si usan HTTPS 5001.
- [ ] Notificacion (correo/webhook) probada con una alerta de prueba.