# Guía de instalación y correcciones del Sistema POS

Guía para que el proyecto funcione correctamente tras una instalación limpia, sin los errores 503 vistos en producción (causa raíz: la base de datos `CommandCenterDb` no existía y el backend seguía arrancando pese a no poder conectar).

## 1. Estado actual de este despliegue (ya corregido)

| Elemento | Estado |
|---|---|
| Base de datos `CommandCenterDb` | Creada en PostgreSQL 18 local |
| Migraciones EF Core | Aplicadas |
| Datos semilla (admin, cliente, producto) | Sembrados |
| `appsettings.json` / `appsettings.Production.json` | Sin secretos en claro |
| Secretos (conexión, admin semilla, JWT, certificado HTTPS) | En `BackendAPI\secrets.json`, archivo protegido con ACL (SYSTEM, Administradores y `NT SERVICE\PosBackendService`) — 8.29-A1 |
| Certificado HTTPS (puerto 5001) | Autofirmado **generado en el puesto** por `scripts/create-https-cert.ps1` (CN/SAN del equipo), contraseña en `secrets.json` |
| Backup de PostgreSQL | Tarea programada 03:00 **activa por defecto** (`Sistema POS - Backup PostgreSQL`), opt-out con `-SkipScheduledBackup` — 8.29-A6 |
| Verificación | `/health` → `Healthy` / `Connected`, `/api/products` → 200 |
| Firewall de Windows (puertos 5000/5001) | Regla de entrada `Sistema POS - Backend API (TCP 5000/5001)` creada, solo subred local |

La convención de configuración está documentada en la §3 de este mismo documento.

## 2. Seguridad implementada en el código fuente

Las siguientes correcciones, originalmente pendientes, ya están integradas en el repositorio:

- **Arranque robusto fail-fast + auto-creación de BD** (`Program.cs`): El backend conecta primero contra la BD de mantenimiento `postgres` para validar credenciales. Si la BD objetivo no existe, la crea automáticamente. Si PostgreSQL no responde, el proceso aborta con código de salida ≠ 0.
- **Seed con `MustChangePassword`**: El usuario admin sembrado queda marcado con `MustChangePassword = true`. La contraseña semilla se lee exclusivamente desde `IConfiguration` (variable de entorno `SystemSettings__AdminSeedPassword`).
- **Login con cambio obligatorio de contraseña**: Si `MustChangePassword == true`, el login responde `403` con `requiresPasswordChange`. El endpoint `POST /api/auth/change-password` permite actualizar la contraseña sin emitir token previo.
- **Cliente WPF**: Si el login devuelve `403`, se presenta el diálogo de cambio de contraseña antes de acceder a la ventana principal.
- **Configuración segura de secretos (8.29-A1)**: Los secretos viven en `BackendAPI\secrets.json`, **no** en variables de entorno del proceso ni en `appsettings.*.json`. El instalador (`setup.iss` / `Configure-PosService.ps1`) escribe el archivo con formato **anidado** y lo protege con ACL. La contraseña del certificado HTTPS nunca viaja por argumentos de línea de comandos.

## 3. Convención de configuración del Backend

La configuración del backend (`Backend.API`) se fusiona en este orden (las fuentes posteriores sobreescriben a las anteriores):

1. `appsettings.json` — valores **compartidos y sin secretos** (fuente única de lo común).
2. `appsettings.{Environment}.json` — solo **overrides por entorno** (`Development`, `Production`, ...). El entorno activo lo define `ASPNETCORE_ENVIRONMENT` (si no se define, el valor por defecto es `Production`).
3. `secrets.json` (máxima precedencia, `AddJsonFile` con `reloadOnChange` en `Program.cs`) — únicamente secretos: `ConnectionStrings.DefaultConnection`, `SystemSettings.AdminSeedPassword`, `JwtSettings.Key`, `Kestrel.Certificates.Default.Password`.

### Qué va en cada archivo

| Archivo | Contenido | Secretos |
|---|---|---|
| `appsettings.json` | Compartido: `Logging`, `AllowedHosts`, `MinimumClientVersion`, `ServerVersion`, `UpdateServerUrl`, `AdminSeedUsername`, `BusinessName` | **Ninguno** |
| `appsettings.Production.json` | Solo overrides reales de producción: `AdminSeedUsername` (`Junior`), `BusinessName` (`Inversiones Junior`) | **Ninguno** |
| `appsettings.Development.json` | Valores de desarrollo local: `ConnectionStrings`, `AdminSeedPassword` | Dev only (aceptable en local) |
| `secrets.json` | Solo secretos (producción), formato **anidado** | **Sí** — protegido con ACL |

### Reglas

- **Una sola fuente por valor.** Si un valor es idéntico en todos los entornos, vive solo en `appsettings.json`; los archivos de entorno no lo repiten.
- **Los secretos nunca van en `appsettings.json`, `appsettings.Production.json` ni en variables de entorno del servicio.** Van en `secrets.json` (producción) o en `appsettings.Development.json` (desarrollo local).
- `UpdateServerUrl` hoy apunta a `localhost:5000` en todos los entornos. Si producción llegara a usar un servidor de actualizaciones remoto, ese es el valor que debe sobrescribirse **solo** en `appsettings.Production.json`.

### Formato de `secrets.json` (producción)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=<secreto>"
  },
  "SystemSettings": {
    "AdminSeedPassword": "<secreto>"
  },
  "JwtSettings": {
    "Key": "<secreto>"
  },
  "Kestrel": {
    "Certificates": {
      "Default": {
        "Password": "<secreto>"
      }
    }
  }
}
```

> **Importante para scripts externos:** .NET interpreta las claves **anidadas** (`ConnectionStrings:DefaultConnection`) en los archivos JSON; las variantes planas `ConnectionStrings__DefaultConnection` solo aplican a variables de entorno. `Configure-PosService.ps1`, `create-https-cert.ps1` y `backup-postgres.ps1` ya leen/escriben el formato anidado (con migración automática desde el legacy si aparece).

## 4. Pasos de instalación en una máquina nueva

1. **Prerrequisitos:** PostgreSQL 18 con usuario `postgres` y contraseña conocida; runtime .NET compatible (el que use el build).
2. **Abrir los puertos en el firewall** (permite que dispositivos externos de la red local accedan al backend; consola elevada):
   ```bat
   netsh advfirewall firewall add rule name="Sistema POS - Backend API (TCP 5000/5001)" dir=in action=allow protocol=TCP localport=5000,5001 remoteip=localsubnet profile=any
   ```
   > La regla queda limitada a la **subred local** (`remoteip=localsubnet`). Para permitir solo una IP concreta, reemplaza `remoteip=localsubnet` por `remoteip=<IP-del-dispositivo>`. Para eliminar la regla: `netsh advfirewall firewall delete rule name="Sistema POS - Backend API (TCP 5000/5001)"`. El puerto 5001 (HTTPS) usa el **certificado de desarrollo**: los clientes externos verán advertencia de certificado.
3. **Copiar binarios** (BackendAPI, DesktopClient, UpdaterService) a la carpeta de instalación.
4. **Registrar el servicio** con NSSM desde una consola elevada:
   ```bat
   nssm install PosBackendService "C:\...\BackendAPI\Backend.API.exe"
   nssm set PosBackendService AppDirectory "C:\...\BackendAPI"
   ```
5. **Crear `BackendAPI\secrets.json` con los secretos** (¡paso crítico!). Una forma segura es ejecutar el instalador (`Configure-PosService.ps1` los genera con CSPRNG y gestiona la ACL automáticamente). A mano, con una consola elevada:

   ```powershell
   $secrets = @{
     ConnectionStrings = @{ DefaultConnection = "Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=<secreto>" }
     SystemSettings     = @{ AdminSeedPassword = "<secreto>" }
     JwtSettings        = @{ Key = "<secreto>" }
     Kestrel            = @{ Certificates = @{ Default = @{ Password = "<secreto>" } } }
   }
   $secrets | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 -LiteralPath "C:\...\BackendAPI\secrets.json"
   icacls "C:\...\BackendAPI\secrets.json" /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" "NT SERVICE\PosBackendService:(R)"
   ```

   > El instalador (Inno Setup / NSSM) crea este archivo por ti y bloquea el acceso salvo SYSTEM/Administradores y la cuenta del servicio. Las claves son **anidadas**; no uses variantes `__`.
6. **Generar el certificado HTTPS del puesto** (si se usará el puerto 5001 con un cliente externo):
   ```powershell
   .\scripts\create-https-cert.ps1 -Subject "CN=<nombre-del-equipo>" -SecretsFile "C:\...\BackendAPI\secrets.json"
   ```
   El script genera un autofirmado con **CN/SAN del equipo**, guarda el certificado y el PFX, y persiste la contraseña en `secrets.json` (nunca por argumentos).
7. **Arrancar**:
   ```bat
   sc start PosBackendService
   ```
   La primera vez, el backend crea la BD (con los cambios de §2.1), aplica migraciones y siembra el admin (que exigirá cambio de contraseña, §2.2–2.3).
8. **Instalar el cliente** en el/los equipos de caja apuntando a `http://localhost:5000` (o la IP del servidor; para ello el firewall del paso 2 debe estar aplicado).

## 5. Checklist de verificación

| # | Prueba | Esperado |
|---|---|---|
| 1 | `curl http://localhost:5000/health` | `200` con `"database":"Connected"` |
| 2 | `curl "http://localhost:5000/api/products?page=1&pageSize=5"` | `200` con items |
| 3 | `curl http://localhost:5000/api/PaymentMethods/active` | `200` con Cash/Card |
| 4 | `curl http://localhost:5000/api/cashdrawer/active-session` | `200` (o 404 controlado si no hay caja abierta) |
| 5 | Login con el admin semilla | `403` con `requiresPasswordChange` |
| 6 | Cambio de contraseña + nuevo login | `200` y acceso normal |
| 7 | `BackendAPI/logs/start.log` | "Database Connection successful", "Migrations applied", sin errores críticos |
| 8 | Reiniciar el servicio con PostgreSQL detenido | El proceso **aborta** con mensaje claro (con §2.1), no arranca en falso |

## 6. Sugerencias adicionales

- **Auto-crear la BD** (§2.1) elimina de raíz el incidente 503 de esta semana: la instalación pasa a ser "copiar, registrar, arrancar".
- **Rotar `Admin123!`** en producción: al estar el cambio de contraseña forzado (§2.2–2.3), el valor inicial deja de ser una exposición permanente.
- **Preservar las env vars al actualizar:** revisar el script del instalador (Inno Setup) y, si re-registra el servicio, que también ejecute el `nssm set AppEnvironmentExtra`.
- **Validar la config en el cliente:** que los mensajes de error de red distingan "servidor apagado", "credenciales de BD" y "versión incompatible" para facilitar el soporte en caja.
- **Acceso de dispositivos externos:** la regla de firewall queda limitada a la subred local. Si una caja está en otra VLAN/subred, añade su IP (`remoteip=<IP>`) en lugar de abrir la regla a todas las redes.
- **HTTPS real en el 5001:** el certificado generado por `create-https-cert.ps1` es autofirmado y por equipo; para clientes externos conviene instalar su entidad raíz de confianza en el servidor o en las cajas, o limitarse a HTTP (5000) dentro de la LAN.
- **Automatizar el firewall en el instalador:** el instalador ya recrea la regla de firewall en cada ejecución (consulta confirmatoria).

## 7. Operación sin conexión a Internet (tasa BCV manual, 8.29-A2)

El sistema sincroniza la tasa oficial del BCV automáticamente (intervalo configurable `BcvSettings:AutoSyncIntervalMinutes`, por defecto 120 min). Si el puesto queda sin red y el auto-sync falla, la operación **continúa** siempre que exista una tasa vigente **para el día**; una venta que no encuentre tasa vigente se rechaza explícitamente (con mensaje al cajero, sin montos inventados).

Procedimiento cuando no hay red:

1. **Desactivar el auto-sync** (opcional, evita reintentos periódicos): en `BackendAPI\appsettings.json` o en la configuración del backend, fijar `BcvSettings:AutoSyncIntervalMinutes` a `0` (o negativo) y reiniciar el servicio. Con esto, la sincronización queda **solo bajo demanda**.
2. **Obtener la tasa** del día (portal del BCV, banca, prensa) e **ingresarla manualmente** como Admin:
   ```http
   POST http://localhost:5000/api/exchange-rate
   Authorization: Bearer <token-admin>
   Content-Type: application/json

   { "value": 73.25 }
   ```
   El backend redondea hacia arriba a 4 decimales (`Math.Ceiling`) y la guarda como la tasa oficial del día. Una vez registrada, la venta usa esa tasa (anclada por petición) aunque la red siga caída.
3. Si la red se recupera, `POST /api/exchange-rate/sync-bcv` fuerza un rastreo manual del portal para corregir el valor si el banco lo ajustó; o se vuelve a activar el auto-sync (intervalo > 0).
4. **Integridad:** la tasa del día queda como `AppliedRate` persistida en cada venta; el historial nunca se recalcula con la tasa actual (`rules.md` §1).

## 8. Backup automático de PostgreSQL (8.29-A6)

El instalador crea **por defecto** la tarea programada `Sistema POS - Backup PostgreSQL` (diaria 03:00, ejecución como SYSTEM) que invoca `scripts/backup-postgres.ps1`:

- Volcado en formato `custom` (`pg_dump -Fc`) con compresión máxima y blobs, destino por defecto `C:\Backups\CommandCenter`.
- Retención de 14 días (configurable con `-RetentionDays`).
- Las credenciales se leen **exclusivamente** de `BackendAPI\secrets.json` (clave `ConnectionStrings.DefaultConnection`), nunca por línea de comandos.
- Se desactiva la creación automática durante la instalación solo con `-SkipScheduledBackup`.

Verificación manual del backup:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files (x86)\Sistema POS Administrador\tools\backup-postgres.ps1" -BackupDir "C:\Backups\CommandCenter"
schtasks /Query /TN "Sistema POS - Backup PostgreSQL"
```

### 8.1. Restauración desde un volcado (8.30-C1)

Procedimiento de restore verificado sobre un volcado `custom` (`pg_dump -Fc`). Ejecutar como **Administrador/SYSTEM** en la máquina del puesto:

```powershell
# 1) Identificar el volcado más reciente y el nombre de la base (debe coincidir con
#    ConnectionStrings.DefaultConnection::Database de BackendAPI\secrets.json).
$backup = Get-ChildItem "C:\Backups\CommandCenter\*.dump" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
# Ej.: C:\Backups\CommandCenter\commandcenter_20260909_030000.dump

# 2) Confirmar el contenido del volcado (listado, sin restaurar).
& "C:\Program Files\PostgreSQL\16\bin\pg_restore.exe" --list $backup.FullName | Select-Object -First 5

# 3) Asegurar que la base destino existe. Si el servicio nunca arrancó (MigrateAsync no la creó),
#    pg_restore --clean --if-exists falla con "database does not exist"; crearla primero:
& "C:\Program Files\PostgreSQL\16\bin\createdb.exe" --username "postgres" --host localhost --port 5432 "commandcenter"

# 4) Restaurar con --clean --if-exists (base ya existente) y --no-owner.
& "C:\Program Files\PostgreSQL\16\bin\pg_restore.exe" --verbose --clean --if-exists --no-owner `
    --username "postgres" --host localhost --port 5432 --dbname "commandcenter" $backup.FullName

# 5) Verificar el smoke tras el restore: la migración de schema es idéntica a la del
#    installer (MigratedSchema) y la ruta de salud responde.
Invoke-RestMethod "http://localhost:5000/health" -Method Get
```

Precauciones: `--no-owner` evita errores si el rol del volcado difiere; detener primero el servicio `Sistema POS Backend` (`Stop-Service "Sistema POS Backend"`) y arrancarlo tras el restore; los snapshots de ventas (`AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS`) se restauran tal cual porque el volcado es una copia punto a punto de la base.
