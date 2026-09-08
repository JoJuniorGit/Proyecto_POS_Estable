# Guía de instalación y correcciones del Sistema POS

Guía para que el proyecto funcione correctamente tras una instalación limpia, sin los errores 503 vistos en producción (causa raíz: la base de datos `CommandCenterDb` no existía y el backend seguía arrancando pese a no poder conectar).

## 1. Estado actual de este despliegue (ya corregido)

| Elemento | Estado |
|---|---|
| Base de datos `CommandCenterDb` | Creada en PostgreSQL 18 local |
| Migraciones EF Core | Aplicadas |
| Datos semilla (admin, cliente, producto) | Sembrados |
| `appsettings.json` / `appsettings.Production.json` | Sin secretos en claro |
| `ConnectionStrings__DefaultConnection` y `SystemSettings__AdminSeedPassword` | Como variables de entorno del servicio `PosBackendService` (NSSM) |
| Verificación | `/health` → `Healthy` / `Connected`, `/api/products` → 200 |
| Firewall de Windows (puertos 5000/5001) | Regla de entrada `Sistema POS - Backend API (TCP 5000/5001)` creada, solo subred local |

La convención de configuración está documentada en la §3 de este mismo documento.

## 2. Seguridad implementada en el código fuente

Las siguientes correcciones, originalmente pendientes, ya están integradas en el repositorio:

- **Arranque robusto fail-fast + auto-creación de BD** (`Program.cs`): El backend conecta primero contra la BD de mantenimiento `postgres` para validar credenciales. Si la BD objetivo no existe, la crea automáticamente. Si PostgreSQL no responde, el proceso aborta con código de salida ≠ 0.
- **Seed con `MustChangePassword`**: El usuario admin sembrado queda marcado con `MustChangePassword = true`. La contraseña semilla se lee exclusivamente desde `IConfiguration` (variable de entorno `SystemSettings__AdminSeedPassword`).
- **Login con cambio obligatorio de contraseña**: Si `MustChangePassword == true`, el login responde `403` con `requiresPasswordChange`. El endpoint `POST /api/auth/change-password` permite actualizar la contraseña sin emitir token previo.
- **Cliente WPF**: Si el login devuelve `403`, se presenta el diálogo de cambio de contraseña antes de acceder a la ventana principal.
- **Configuración de `appsettings`**: Los secretos se inyectan vía variables de entorno del servicio NSSM. Los archivos JSON no contienen credenciales en producción.

## 3. Convención de configuración del Backend

La configuración del backend (`Backend.API`) se fusiona en este orden (las fuentes posteriores sobreescriben a las anteriores):

1. `appsettings.json` — valores **compartidos y sin secretos** (fuente única de lo común).
2. `appsettings.{Environment}.json` — solo **overrides por entorno** (`Development`, `Production`, ...). El entorno activo lo define `ASPNETCORE_ENVIRONMENT` (si no se define, el valor por defecto es `Production`).
3. Variables de entorno del proceso (convención `Seccion__Clave`).

### Qué va en cada archivo

| Archivo | Contenido | Secretos |
|---|---|---|
| `appsettings.json` | Compartido: `Logging`, `AllowedHosts`, `MinimumClientVersion`, `ServerVersion`, `UpdateServerUrl`, `AdminSeedUsername`, `BusinessName` | **Ninguno** |
| `appsettings.Production.json` | Solo overrides reales de producción: `AdminSeedUsername` (`Junior`), `BusinessName` (`Inversiones Junior`) | **Ninguno** |
| `appsettings.Development.json` | Valores de desarrollo local: `ConnectionStrings`, `AdminSeedPassword` | Dev only (aceptable en local) |

### Reglas

- **Una sola fuente por valor.** Si un valor es idéntico en todos los entornos, vive solo en `appsettings.json`; los archivos de entorno no lo repiten.
- **Los secretos nunca van en `appsettings.json` ni en `appsettings.Production.json`.** Van en variables de entorno del proceso (producción) o en `appsettings.Development.json` (desarrollo local).
- `UpdateServerUrl` hoy apunta a `localhost:5000` en todos los entornos. Si producción llegara a usar un servidor de actualizaciones remoto, ese es el valor que debe sobrescribirse **solo** en `appsettings.Production.json`.

### Variables de entorno requeridas en producción

El servicio `PosBackendService` (registrado con NSSM) necesita estas variables. Sin ellas, el backend no tiene credenciales de base de datos ni contraseña semilla:

| Variable | Valor (este despliegue) | Qué configura |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres` | Cadena de conexión a PostgreSQL |
| `SystemSettings__AdminSeedPassword` | `Admin123!` | Contraseña inicial del usuario admin (solo se usa al sembrar si el usuario no existe) |

Se guardan en el registro en `HKLM\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters\AppEnvironmentExtra` (REG_MULTI_SZ).

> **Importante:** si el instalador vuelve a registrar el servicio (NSSM) durante una actualización, estas variables se pierden y hay que volver a establecerlas. Si el arranque falla con "las credenciales son incorrectas", revisa primero que las variables estén presentes.

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
5. **Establecer las variables de entorno del servicio** (¡paso crítico!):
   ```bat
   nssm set PosBackendService AppEnvironmentExtra ^
     "SystemSettings__AdminSeedPassword=Admin123!" ^
     "ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres"
   ```
   > Si el instalador (Inno Setup / NSSM) re-registra el servicio en cada actualización, estas variables se pierden: añádelas al script del instalador o a un script de primer arranque. Sin `ConnectionStrings__DefaultConnection`, el backend no tiene dónde conectar.
6. **Arrancar**:
   ```bat
   sc start PosBackendService
   ```
   La primera vez, el backend crea la BD (con los cambios de §2.1), aplica migraciones y siembra el admin (que exigirá cambio de contraseña, §2.2–2.3).
7. **Instalar el cliente** en el/los equipos de caja apuntando a `http://localhost:5000` (o la IP del servidor; para ello el firewall del paso 2 debe estar aplicado).

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
- **HTTPS real en el 5001:** el certificado actual es el de desarrollo; para clientes externos conviene instalar un certificado de confianza en el servidor o limitarse a HTTP (5000) dentro de la LAN.
- **Automatizar el firewall en el instalador:** igual que con las env vars, si el instalador se re-ejecuta, que también verifique/recree la regla de firewall (añadirla al script de Inno Setup o a un script de primer arranque).
