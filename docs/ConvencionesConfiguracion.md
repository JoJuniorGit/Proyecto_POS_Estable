# Sistema POS Administrador — Convención de configuración

Documento cómo se organiza la configuración del backend (`BackendAPI`) para evitar divergencias entre entornos y mantener los secretos fuera de los archivos.

## Jerarquía de configuración (ASP.NET Core)

La configuración se fusiona en este orden (las fuentes posteriores sobreescriben a las anteriores):

1. `appsettings.json` — valores **compartidos y sin secretos** (fuente única de lo común).
2. `appsettings.{Environment}.json` — solo **overrides por entorno** (`Development`, `Production`, ...). El entorno activo lo define `ASPNETCORE_ENVIRONMENT` (si no se define, el valor por defecto es `Production`).
3. Variables de entorno del proceso (convención `Seccion__Clave`).

## Qué va en cada archivo

| Archivo | Contenido | Secretos |
|---|---|---|
| `appsettings.json` | Compartido: `Logging`, `AllowedHosts`, `MinimumClientVersion`, `ServerVersion`, `UpdateServerUrl`, `AdminSeedUsername`, `BusinessName` | **Ninguno** |
| `appsettings.Production.json` | Solo overrides reales de producción: `AdminSeedUsername` (`Junior`), `BusinessName` (`Inversiones Junior`) | **Ninguno** |
| `appsettings.Development.json` | Valores de desarrollo local: `ConnectionStrings`, `AdminSeedPassword` | Dev only (aceptable en local) |

### Reglas

- **Una sola fuente por valor.** Si un valor es idéntico en todos los entornos, vive solo en `appsettings.json`; los archivos de entorno no lo repiten.
- **Los secretos nunca van en `appsettings.json` ni en `appsettings.Production.json`.** Van en variables de entorno del proceso (producción) o en `appsettings.Development.json` (desarrollo local).
- `UpdateServerUrl` hoy apunta a `localhost:5000` en todos los entornos. Si producción llegara a usar un servidor de actualizaciones remoto, ese es el valor que debe sobrescribirse **solo** en `appsettings.Production.json`.

## Variables de entorno requeridas en producción

El servicio `PosBackendService` (registrado con NSSM) necesita estas variables. Sin ellas, el backend no tiene credenciales de base de datos ni contraseña semilla:

| Variable | Valor (este despliegue) | Qué configura |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | `Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres` | Cadena de conexión a PostgreSQL |
| `SystemSettings__AdminSeedPassword` | `Admin123!` | Contraseña inicial del usuario admin (solo se usa al sembrar si el usuario no existe) |

### Cómo se establecen (una vez, con consola elevada)

```bat
nssm set PosBackendService AppEnvironmentExtra ^
  "SystemSettings__AdminSeedPassword=Admin123!" ^
  "ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=postgres"
sc restart PosBackendService
```

Se guardan en el registro en `HKLM\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters\AppEnvironmentExtra` (REG_MULTI_SZ).

> **Importante:** si el instalador vuelve a registrar el servicio (NSSM) durante una actualización, estas variables se pierden y hay que volver a establecerlas. Si el arranque falla con "las credenciales son incorrectas", revisa primero que las variables estén presentes.

## Acceso de red al backend (puertos 5000/5001)

El backend escucha en **todas las interfaces** en los puertos **5000 (HTTP)** y **5001 (HTTPS)**. Para que un dispositivo externo (otra caja, tablet, PC) acceda desde la red local, el Firewall de Windows debe permitir la entrada:

```bat
netsh advfirewall firewall add rule name="Sistema POS - Backend API (TCP 5000/5001)" dir=in action=allow protocol=TCP localport=5000,5001 remoteip=localsubnet profile=any
```

- La regla se limita a la **subred local** (`remoteip=localsubnet`) para no exponer el backend a internet. Para permitir solo una IP concreta, reemplaza `remoteip=localsubnet` por `remoteip=<IP-del-dispositivo>`.
- El puerto 5001 usa el **certificado de desarrollo**: los clientes externos verán advertencia de certificado hasta que se instale uno de confianza.
- El procedimiento completo de instalación (incluido el firewall) está en `INSTALLATION.md`.

## Seguridad pendiente (requiere código fuente)

Los siguientes cambios **no se pueden aplicar sobre los binarios compilados**; se hacen en el repositorio C# (`Backend.API` / `Inventory.Module` / `Sales.Module`) y se compilan con el SDK de .NET. Los pasos exactos de implementación están documentados en `INSTALLATION.md` (§2):

- **Forzar cambio de contraseña en el primer inicio de sesión** (`INSTALLATION.md` §2.2–2.4): marcar al admin sembrado como `MustChangePassword = true`, que el login devuelva `403` con `requiresPasswordChange` y añadir el endpoint `POST /api/auth/change-password`.
- **Validación de arranque fail-fast** (`INSTALLATION.md` §2.1): si la conexión a PostgreSQL falla, el proceso debe **abortar** con un mensaje claro y código de salida distinto de cero (y auto-crear la BD si falta), en lugar de seguir sirviendo peticiones que devolverán 503.

Mientras esos cambios no estén, la contraseña semilla documentada arriba (`Admin123!`) debe rotarse manualmente en el primer login.
