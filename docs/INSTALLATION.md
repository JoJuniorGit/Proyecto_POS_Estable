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

### 5.1. Paridad instalador ↔ backend (IMP-2, 8.67-B2)

Checklist de paridad para verificar que el instalador (Inno Setup) y el backend validan los mismos valores. Cualquier desviación aquí puede producir un usuario que el backend rechaza al sembrar el admin (8.63).

| Regla | Instalador (`setup.iss`) | Backend (`PasswordPolicyService`) |
|---|---|---|
| Longitud mínima | `>= 4` caracteres | `>= 4` caracteres |
| Longitud máxima | sin límite en el formulario | `<= 128` caracteres |
| Composición | al menos 1 letra y 1 número | al menos 1 letra y 1 número |
| Blacklist de comunes | no validada | sí (28 entradas + custom) |
| Contiene username/cédula | no validado | rechazado (nombre y dígitos de cédula >= 4) |
| Caracteres ambiguos en generación | n/a (no genera) | alfabeto sin `0/O/l/I/8/B` |

> Procedimiento: tras instalar y antes de arrancar, ejecutar `PasswordPolicyService.ValidatePassword` con la contraseña del formulario del instalador. Si el valor elegido la rechaza, configurar la contraseña directamente vía `secrets.json`/backend con una que cumpla la política completa. El seed del admin se valida en el arranque (`DatabaseInitializer.cs`) y aborta (fail-fast) si no cumple la política.

Puertos y secretos:

- **Puertos:** establecer los mismos `5000` (HTTP) / `5001` (HTTPS) en el firewall del instalador y en `Kestrel`/`UpdateServerUrl` del backend (`appsettings.json`).
- **ACL de `secrets.json`:** el instalador (`Configure-PosService.ps1`) aplica `SYSTEM:(F)` / `Administrators:(F)` / cuenta del servicio `(R)` con herencia deshabilitada; respetar esa ACL en restauraciones manuales (§8.2).
- **Tópico de `secrets.json`:** solo secretos anidados (`ConnectionStrings:DefaultConnection`, `SystemSettings:AdminSeedPassword`, `JwtSettings:Key`, `Kestrel:Certificates.Default.Password`); nunca `__` plano ni variables de entorno del servicio.

## 6. Sugerencias adicionales

- **Auto-crear la BD** (§2.1) elimina de raíz el incidente 503 de esta semana: la instalación pasa a ser "copiar, registrar, arrancar".
- **Rotar `Admin123!`** en producción: al estar el cambio de contraseña forzado (§2.2–2.3), el valor inicial deja de ser una exposición permanente.
- **Preservar las env vars al actualizar:** revisar el script del instalador (Inno Setup) y, si re-registra el servicio, que también ejecute el `nssm set AppEnvironmentExtra`.
- **Validar la config en el cliente:** que los mensajes de error de red distingan "servidor apagado", "credenciales de BD" y "versión incompatible" para facilitar el soporte en caja.
- **Acceso de dispositivos externos:** la regla de firewall queda limitada a la subred local. Si una caja está en otra VLAN/subred, añade su IP (`remoteip=<IP>`) en lugar de abrir la regla a todas las redes.
- **HTTPS real en el 5001:** el certificado generado por `create-https-cert.ps1` es autofirmado y por equipo; para clientes externos conviene instalar su entidad raíz de confianza en el servidor o en las cajas, o limitarse a HTTP (5000) dentro de la LAN.
- **Automatizar el firewall en el instalador:** el instalador ya recrea la regla de firewall en cada ejecución (consulta confirmatoria).

## 6A. Windows Defender / SmartScreen en la caja LAN (IMP-4, 8.67-B4)

Los binarios del sistema son **self-contained** (no dependen del runtime instalado) y se ejecutan como servicio vía **NSSM**; Windows Defender puede marcar los ejecutables nuevos (especialmente tras cada release) y SmartScreen puede bloquear el instalador. Excluir operativas documentadas (además de la firma X.509, pendiente en P13):

1. **SmartScreen / Mark-of-the-Web:** si el instalador o los binarios llegan descargados (ZIP), desbloquear con `Unblock-File` antes de ejecutar. Para instalaciones internas firmadas, SmartScreen no debe pedir confirmación una vez la marca de descarga se elimina.
2. **Exclusiones de Microsoft Defender (por carpeta del puesto):** añadir como exclusiones de proceso/archivo las carpetas de instalación (p. ej. `C:\Program Files (x86)\Sistema POS Administrador\`) y de datos/backup (`C:\Backups\CommandCenter\`). Comando (consola elevada):

   ```powershell
   Add-MpPreference -ExclusionPath "C:\Program Files (x86)\Sistema POS Administrador"
   Add-MpPreference -ExclusionPath "C:\Backups\CommandCenter"
   ```

3. **Servicio NSSM (`PosBackendService`):** el proceso `Backend.API.exe` servido por NSSM no debe ser bloqueado al arrancar; la exclusión por carpeta de la instalación cubre `Backend.API.exe`, `Desktop.Client.exe`, `UpdaterService.exe` y `resources\` (runtime self-contained).
4. **Verificación post-exclusión:** `Get-MpPreference | Select-Object -ExpandProperty ExclusionPath` y reiniciar el servicio para confirmar que no queda `0x80070005`/bloqueos de Defender en el arranque.

> La exclusión es **operativa y local al puesto**, no sustituye la firma X.509 del release (P13/F3): el binario firmado reduce la dependencia de exclusiones y habilita SmartScreen silencioso en las cajas.

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

### 8.2. Respaldo y recuperación de la configuración del sitio (IMP-3, 8.67-B3)

El volcado de PostgreSQL (§8) respalda **solo la base de datos**; ante pérdida total del equipo (disco/caja), la configuración del sitio debe restaurarse también. La copia de seguridad operativa del sitio incluye además:

| Elemento | Ruta (puesto) | Contenido |
|---|---|---|
| `secrets.json` | `BackendAPI\secrets.json` | Cadena de conexión (password de PostgreSQL), `AdminSeedPassword`, `JwtSettings.Key`, password del certificado |
| Certificado HTTPS por sitio | `BackendAPI\certs` (PFX + `.cer` público) | Identidad del puesto para el puerto 5001 |
| `client_settings.json` | carpeta del cliente en cada caja | Configuración del cliente desktop por caja |
| Credenciales de PostgreSQL | información del formulario del instalador | Usuario/contraseña `postgres` (hoy conocida por el operador) |

Paso de **reinstalación** ante pérdida total (restaura la config previa):

1. Reinstalar el paquete del instalador en la máquina nueva.
2. **Restaurar `secrets.json`** del respaldo del sitio en `BackendAPI\secrets.json` con la misma ACL (`icacls ... /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" "NT SERVICE\PosBackendService:(R)"`), o hacer que el instalador regenere los secretos (CSPRNG) si el respaldo no existe.
3. **Restaurar el certificado** `certs\` (PFX) y verificar que la `Kestrel.Certificates.Default.Password` del `secrets.json` casa con ese PFX (§13.2).
4. **Restaurar la base** desde el volcado más reciente (§8.1).
5. Verificar `/health` y la lista de cajas (cada caja restaura su `client_settings.json` apuntando al puesto).

## 9. Limitaciones pre-piloto (8.31-B2)

- **Impresión de recibos y cierres de caja:** los recibos de venta (factura digital NO fiscal / nota de entrega) y los cierres se generan como **PDF** y se guardan en segundo plano (carpeta `Receipts\` del backend para los recibos; `Documentos\Registro de cierres` y `Descargas` para los cierres), de forma **no bloqueante** para el cajero. No hay salida a impresora térmica física todavía (se puede imprimir el PDF desde la carpeta); confirmar esta expectativa con el cliente antes del piloto.

## 9A. Factura digital NO fiscal (8.50)

Cada venta completada emite un comprobante NO fiscal (recibo / nota de entrega) en PDF de forma **asíncrona y no bloqueante**:

- Al completarse la venta (`CompleteSaleAsync`), el flujo **encola** el snapshot de la venta en una cola `Channel` (fire-and-forget) — el cajero no espera.
- Un servicio en segundo plano (`ReceiptPrintBackgroundService`) drena la cola, **genera el PDF** (`SaleReceiptPdfGenerator`) usando exclusivamente los **snapshots inmutables** (`AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS`, `RoundingAdjustment` — `rules.md` §1) y lo guarda en `<backend>\Receipts\`.
- Nombre del archivo: `Recibo_<FacturaD5>_<SaleId>_<fecha>.pdf`.
- Los montos se formatean con cultura invariante (punto decimal) para consistencia del documento.
- El renderer y la cola se registran en DI (`IReceiptDocumentRenderer` singleton, `IReceiptPrintQueue` singleton + hosted service).
- La salida es **best-effort no durable**: si el servicio se reinicia entre el commit y el guardado, el recibo de esa venta no se re-emite (adecuado para comprobante NO fiscal; la venta ya queda persistida).

### Acceso desde la caja (8.51)

En el flujo de **venta completada** (WPF), tras el modal de éxito, el cajero puede confirmar "¿Desea abrir el recibo (PDF)?" y la aplicación obtiene el PDF on-demand vía `GET /api/sales/{id}/receipt` (endpoint protegido Admin/Manager/Cashier), lo guarda en `%TEMP%\CommandCenterReceipts` y lo abre con el visor PDF del sistema. Este endpoint **regenera el PDF de forma síncrona** a partir del snapshot persistido de la venta (independiente de la emisión asíncrona de la carpeta `Receipts\`).

Sin impresora física térmica integrada: el PDF se abre/conserva para imprimir. La integración de impresora térmica asíncrona es trabajo futuro sobre esta base.
- **Actualizaciones automáticas del cliente:** el UpdaterService no se empaqueta en el instalador (8.20-M08); el rol se validará con firma X.509 (8U-N2).
- **Multi-sucursal:** sin `BranchId` todavía (intención arquitectónica futura, no requisito del piloto; ver `coding-guidelines.md` §5).
- **Certificado HTTPS autofirmado:** los clientes web/WPF verán una advertencia "no confiable" al primer acceso por host remoto; para evadirla, importar el `.cer` del puesto en el almacén raíz de confianza de cada caja (ver §6).

## 10. Plan de actualización y rollback de esquema (Fase 2, 8.39)

### 10.1 Política de versionado de esquema

- El esquema se versiona **exclusivamente** con migraciones EF (`dotnet ef migrations add`). Toda evolución de modelo persistente DEBE acompañarse de una migración con atributo `[Migration]` (regla de `rules.md`/`AGENTS`; previene incidentes tipo 8.20-C01).
- Invariante obligatorio antes de cada release: `dotnet ef migrations has-pending-model-changes` limpio en **ambos** contextos (`SalesDbContext` e `InventoryDbContext`).
- Verificación por ejecución real: el smoke `MigratedSchema` valida que `MigrateAsync()` aplica el esquema y que las columnas/índices críticos existen (p. ej. `StockMovement.SaleId`).

### 10.2 Procedimiento de actualización (upgrade)

1. **Respaldar antes de actualizar:** ejecutar el backup programado (`backup-postgres.ps1`) o un `pg_dump -Fc` manual; confirmar que el volcado es reciente.
2. **Publicar el nuevo artefacto** Release firmado (ver Fase 4) y copiarlo al puesto.
3. **Detener el servicio** `Sistema POS Backend` (`Stop-Service`).
4. **Reemplazar los binarios** preservando `BackendAPI\secrets.json` (las credenciales por sitio no viajan en el publish).
5. **Iniciar el servicio**: el arranque ejecuta `MigrateAsync()` (fail-fast: si la migración falla, aborta sin servir tráfico).
6. **Verificar** con el smoke `MigratedSchema` y `GET /health`; confirmar que el esquema migró sin errores.

### 10.3 Procedimiento de rollback

- **Si el esquema no cambió** (release solo de código): basta reemplazar los binarios por la versión anterior y reiniciar.
- **Si el esquema cambió y la migración es aditiva/no destructiva:** suele bastar con degradar los binarios; la base queda compatible hacia atrás.
- **Si el esquema cambió y se necesita revertir datos:** restaurar el volcado previo (`pg_restore --clean --if-exists --no-owner`, ver §8.1) sobre la base, deteniendo antes el servicio, y arrancar la versión anterior.
- Regla de integridad: el rollback nunca recalcula el historial de ventas; los snapshots (`AppliedRate`, `TotalUSD`, `TotalBsS`, `FinalPaidAmountBsS`) se restauran tal cual del volcado (ver `rules.md` §1).

## 11. Matriz de configuración por ambiente (Fase 4, 8.43)

| Parámetro | Desarrollo | QA | Piloto | Producción |
|-----------|-----------|-----|--------|-----------|
| `ASPNETCORE_ENVIRONMENT` | Development | Production | Production | Production |
| `ConnectionStrings.DefaultConnection` | local dev | BD QA dedicada | BD del puesto | BD del puesto (secrets.json) |
| `SystemSettings.AdminSeedPassword` | dev-only (appsettings.Development) | CSPRNG por sitio | CSPRNG por sitio | CSPRNG por sitio (secrets.json) |
| `JwtSettings.Key` | clave dev (bloqueada en prod) | CSPRNG 512 bits | CSPRNG 512 bits | CSPRNG 512 bits (secrets.json) |
| `Kestrel.Certificates.Default.Password` | - | por sitio | por sitio | por sitio (secrets.json) |
| `SecuritySettings:RequireHttpsMetadata` | false | false (LAN) | false (LAN) | true si no es LAN aislada |
| `BcvSettings:AutoSyncIntervalMinutes` | 120 | 120 | 120 (o 0 offline) | 120 (o 0 offline) |
| `appsettings.Development.json` en publish | - | NO | NO | NO (excluido 8.29-A4) |

## 12. Checklist de instalación por cliente (Fase 4, 8.43)

Preparación del sitio (una sola vez):

1. **PostgreSQL 16/18** instalado y arrancado como servicio; credencial `postgres` con privilegio `CREATEDB`.
2. **Requisitos de red**: puertos 5000/5001 abiertos en firewall (subred local); decidir HTTP/HTTPS (DQ-006).
3. **Instalar** el paquete del instalador (Inno Setup) y ejecutarlo como Administrador.
4. **Formulario**: host/puerto/BD/usuario postgres + contraseña; usuario admin + nombre + contraseña (debe cumplir política); nombre del negocio.
5. **Verificar** que `Configure-PosService.ps1` registró: servicio NSSM `Sistema POS Backend` (Virtual Account + ACL), reglas de firewall, `secrets.json` con ACL, certificado HTTPS por sitio y tarea de backup diaria 03:00.
6. **Probar** `GET http://localhost:5000/health` y el smoke `MigratedSchema`.
7. **Cajas**: importar el `.cer` del puesto en la raíz de confianza de cada caja (si usan HTTPS 5001).
8. **Cambio obligatorio de contraseña** del admin en el primer login (MustChangePassword).

Checklist de release (por versión):

1. `dotnet build CommandCenter.slnx -c Release`: 0 warnings / 0 errores.
2. `dotnet test` (con `TEST_POSTGRES_CONNECTION`): suite completa verde (754).
3. `npm run lint` y `npm test` (Web.Frontend): 0 / 80.
4. `scripts/build-release.ps1`: publish con scrub OK (0 literales, 0 `*.pfx`, sin `appsettings.Development.json`).
5. `dotnet ef migrations has-pending-model-changes` limpio en ambos contextos (si cambió el modelo).
6. Backup del puesto previo al despliegue (ver §10.2).

## 13. Runbooks operativos (Fase 5, 8.44)

### 13.1 Servicio backend caído
1. `Get-Service "Sistema POS Backend"` → si `Stopped`, `Start-Service`.
2. Revisar `logs\crash.log` y `logs\start.log` del directorio del backend.
3. Si falla al arrancar: validar `secrets.json` (connection string/JWT/cert password) y que PostgreSQL esté arriba (`pg_isready`).
4. Tras recuperar, validar `GET http://localhost:5000/health` (status Healthy + database Connected).

### 13.2 Certificado HTTPS inválido / expirado
1. `Configure-PosService.ps1` regenera el pfx por sitio cuando detecta pfx stale o contraseña nueva (sección 4.2) y lo coloca en `BackendAPI\certs`.
2. Verificar que `secrets.json` (`Kestrel.Certificates.Default.Password`) casa con el pfx.
3. Reiniciar el servicio; validar `https://localhost:5001` (o distribuir el nuevo `.cer` en las cajas).

### 13.3 Tasa BCV no disponible / sin internet
1. Si no hay red: fijar `BcvSettings:AutoSyncIntervalMinutes` ≤ 0 y reiniciar.
2. Registrar la tasa del día como Admin: `POST /api/exchange-rate { "value": <tasa> }` (ceil 4 decimales). Sin tasa vigente, la venta se rechaza explícitamente (sin montos inventados). Detalle: §7.

### 13.4 Stock inconsistente / sospecha de sobreventa
1. Revisar `StockMovements` del producto y el historial de la venta (los snapshots no se recalculan, §10/rules.md).
2. Si hay discrepancia real, ajustar manualmente con permiso de Admin (`AdjustStock`) y registrar el movimiento.
3. Validar que no hubo deducción doble (idempotencia por SaleId).

### 13.5 Restore / rollback
1. Restore: §8.1 (`pg_restore --clean --if-exists --no-owner`; detener el servicio antes; smoke vía `/health`).
2. Rollback de versión: §10.3 (binarios previos o restore del volcado; sin recalcular historial).

### 13.6 Backup fallando
1. Verificar la tarea programada `Sistema POS - Backup PostgreSQL` (`schtasks /Query`) y ejecutar `backup-postgres.ps1` manualmente.
2. Comprobar espacio en disco del destino (`C:\Backups\CommandCenter`) y las credenciales en `secrets.json`.
3. Confirmar que el último volcado es reciente antes de cualquier operación de restore.

### 13.7 Responsable y ventana de mantenimiento
1. Designar un responsable operativo en el sitio (RQ de la Fase 0).
2. Ventana de mantenimiento recomendada: nocturna (fuera de horario de caja); el backup corre a las 03:00.

### 13.8 Monitoreo mínimo del piloto (F5/8.80)
1. Programar `docs\monitor-health.ps1` con el Programador de tareas cada 5 min
   (`HealthUrl=http://localhost:5000/health`, `DetailsUrl=http://localhost:5000/api/health/details`,
   `NotifyUrl=<webhook opcional>`), como recomienda roadmap §9.
2. El script incrementa un contador de fallos consecutivos y notifica al tercer fallo
   (`FailsToAlert`); también alerta si el último backup no está fresco (RPO).
3. Verificar `schtasks /Query` y el log `monitor.log`; un fallo aislado no es alerta.
4. Para supervisión remota del piloto: VPN WireGuard (DQ-007 opción B), no exponer puertos a Internet.

### 13.9 Paridad instalador ↔ backend post-instalación (IMP-2/8.80)
Ejecutar justo después de instalar/actualizar, contra el checklist de §5.1:
1. Crear el admin con la contraseña del formulario y confirmar que el backend **acepta** exactamente
   lo que el instalador recomienda (misma política: longitud, composición, blacklist).
2. Crear una cuenta con la contraseña que el instalador **rechaza** y confirmar que el backend la
   **rechaza** igualmente (401/400). Cualquier divergencia = paridad rota → usar `PasswordPolicyService`
   como fuente y corregir `setup.iss` antes de seguir.
3. Confirmar puertos 5000/5001 y ACL de `secrets.json` (§3) tras la instalación.
4. Registrar el resultado en el checklist del cliente (§12).

### 13.10 Pre-despliegue y verificación del artefacto (F4/8.80)
Antes de instalar una Release Candidate en el puesto (o en QA), validar el artefacto:
1. `scripts\build-release.ps1` con `ISCC_PATH` definido: verifica bundle web en `wwwroot`
   (index.html + assets), scrub de secretos, 0 `*.pfx`, 0 `appsettings.Development.json`.
2. Prerequisito del smoke (paso 7/7): asegurar binarios Release de las pruebas
   (`dotnet build CommandCenter.slnx -c Release`) antes de `dotnet test --no-build`.
3. Ejecutar el paso de smoke (7/7) definiendo `TEST_POSTGRES_CONNECTION`: corre
   `MigratedSchema` (incl. el smoke de BD vacía IMP-1).
4. `dotnet build CommandCenter.slnx -c Release` (0/0) y `dotnet ef migrations has-pending-model-changes`
   limpio en ambos contextos si cambió el modelo.
5. Instalar en el puesto por §12 checklist e inmediatamente ejecutar §13.9 (paridad).
6. Firma X.509 e ISCC firmado y smoke en máquina virgen quedan PENDIENTES hasta disponer de
   certificado y entorno QA/VM (roadmap §8, items sin marcar).

## 14. Plan del piloto controlado (Fase 6, 8.45)

Alcance y reglas:

1. **Versión congelada:** desplegar la Release Candidate (V0.15) en UNA sucursal; durante el piloto NO se incorporan funcionalidades nuevas (solo correcciones aprobadas).
2. **Duración inicial:** 1-2 semanas.
3. **SLOs de referencia:** `RPO <= 24 h`, `RTO <= 4 h`, checkout sin duplicados, sin pérdida de datos (ver roadmap §3.3).
4. **Sin cambios de esquema** no planificados; cualquier migración se trata como una actualización (§10.2) con backup previo.

Registro diario (por el responsable del sitio):

| Día | Ventas | Cierres | Errores | Latencia (checkout) | Backup OK (03:00) | Intervenciones |
|-----|--------|---------|---------|---------------------|-------------------|----------------|
|     |        |         |         |                     |                   |                |

Actividades durante el piloto:

1. Revisión diaria de incidencias y de la tabla de registro; sin funcionalidades nuevas.
2. Ejecutar al menos **un restore de validación** en una copia aislada (vía §8.1) y confirmar conteos (ventas/pagos/tasas/cierres) e integridad de snapshots.
3. Verificar diariamente el backup (tarea `Sistema POS - Backup PostgreSQL`) y `/health`.
4. Confirmar operación offline de la tasa BCV (§7) si el puesto queda sin red.

Criterios de aceptación (M6) — se cumplen TODOS:

- [ ] Cero incidentes críticos y cero pérdida de datos durante el piloto.
- [ ] Operación dentro de los SLOs de referencia (§3).
- [ ] Backup diario OK y al menos un restore de validación exitoso.
- [ ] Sin duplicados de venta/abono bajo reintentos (idempotencia).
- [ ] Aceptación formal del cliente (firma Go/No-Go de Fase 6).
