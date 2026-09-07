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

Los cambios de configuración de esta máquina están documentados en `README.md`.

## 2. Correcciones que deben aplicarse en el código fuente

El repositorio C# vive en `C:\Users\Lenovo IdeaPad 3\Desktop\Proyecto_POS_Estable\V0.1\` (rutas inferidas de los stack traces; ajustar nombres reales de archivos si difieren). Sin estos cambios, una instalación limpia volverá a fallar.

### 2.1 `Backend.API/Program.cs` — arranque robusto (fail-fast + auto-crear BD)

**Problema actual:** el chequeo `CanConnectAsync` se hace contra la base de datos objetivo. Si la BD no existe, parece un fallo de credenciales y, además, el error solo se registra y la app sigue arrancando sirviendo 503.

**Cambio:** conectar primero contra la BD de mantenimiento `postgres`, crear la BD si falta, aplicar migraciones y **abortar** el arranque si algo falla:

```csharp
// 1) Probar conexión contra la BD de mantenimiento "postgres" para distinguir
//    credenciales incorrectas de base de datos inexistente.
var maintenanceCs = new NpgsqlConnectionStringBuilder(connectionString)
{
    Database = "postgres"
}.ConnectionString;

await using var probe = new NpgsqlConnection(maintenanceCs);
try
{
    await probe.OpenAsync(ct);
}
catch (Exception ex)
{
    logger.LogCritical("[ERROR CRÍTICO] No se pudo conectar a PostgreSQL. " +
        "Verifique que el servicio de BD esté activo y que la variable de entorno " +
        "ConnectionStrings__DefaultConnection (o el appsettings) sea correcta. {0}", ex.Message);
    Environment.ExitCode = 1;
    return; // NO seguir arrancando: evita servir peticiones que devuelven 503
}

// 2) Crear la base de datos si no existe (idempotente).
await using (var cmd = probe.CreateCommand())
{
    cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @db";
    cmd.Parameters.AddWithValue("db", dbName);
    var exists = await cmd.ExecuteScalarAsync(ct) != null;
    if (!exists)
    {
        logger.LogInformation("[START] Creando base de datos {Db}...", dbName);
        await using var create = probe.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{dbName}\" OWNER postgres";
        await create.ExecuteNonQueryAsync(ct);
    }
}

// 3) Aplicar migraciones (crea el esquema y __EFMigrationsHistory).
await db.Database.MigrateAsync(ct);
```

> `MigrateAsync` sobre Npgsql crea la BD si no existe, pero conviene el paso 2 explícito para que el mensaje de error sea claro cuando las credenciales fallan.

### 2.2 Seed del admin — contraseña desde configuración/env y cambio obligatorio

**Problema actual:** la contraseña semilla viene de `AdminSeedPassword` en config; si la clave falta (tras quitarla de los JSON), el seed puede crear un usuario roto.

**Cambio:**
1. En el código del seed, leer la contraseña solo desde `IConfiguration` (la variable de entorno `SystemSettings__AdminSeedPassword` la sobreescribe automáticamente):
   ```csharp
   var seedPassword = config["SystemSettings:AdminSeedPassword"];
   if (string.IsNullOrWhiteSpace(seedPassword))
   {
       logger.LogCritical("[ERROR CRÍTICO] Falta SystemSettings__AdminSeedPassword. " +
           "Establezca la variable de entorno del servicio antes de arrancar.");
       Environment.ExitCode = 1;
       return;
   }
   ```
2. Añadir `public bool MustChangePassword { get; set; }` a la entidad `User` (migración EF nueva) y marcar `MustChangePassword = true` al usuario admin creado por el seed.

### 2.3 Login — forzar cambio de contraseña en el primer acceso

En el controlador/servicio de autenticación (`AuthController`/`AuthService`), tras validar credenciales:

```csharp
if (user.MustChangePassword)
{
    return StatusCode(403, new
    {
        requiresPasswordChange = true,
        message = "Debe cambiar su contraseña antes de continuar."
    });
}
```

Nuevo endpoint (no emite token hasta que se complete el cambio):

```csharp
[HttpPost("change-password")]
public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
{
    var user = await _userService.GetByUsernameAsync(req.Username);
    if (user is null || !VerifyPassword(req.CurrentPassword, user.PasswordHash))
        return Unauthorized(new { message = "Usuario o contraseña actual incorrectos." });

    user.PasswordHash = HashPassword(req.NewPassword);
    user.MustChangePassword = false;
    await _userService.UpdateAsync(user);
    return Ok(new { message = "Contraseña actualizada." });
}
```

### 2.4 Cliente WPF (`Desktop.Client`) — flujo de cambio de contraseña

- Si el login responde `403` con `requiresPasswordChange = true`, abrir un diálogo "Cambiar contraseña" (actual + nueva) en lugar de mostrar la ventana principal.
- Llamar a `POST /api/auth/change-password` y, al obtener 200, reintentar el login con la nueva contraseña.
- Mantener la resiliencia ya existente (reintentos + sondeo de `/health`).

### 2.5 `appsettings.*` en el repositorio fuente

Aplicar la misma estructura que en el despliegue (ver `README.md`):
- `appsettings.json`: solo valores compartidos, **sin secretos**.
- `appsettings.Production.json`: solo `AdminSeedUsername` y `BusinessName`.
- `appsettings.Development.json`: cadena de conexión y `AdminSeedPassword` de desarrollo.
- Los secretos de producción se inyectan como variables de entorno del servicio.

## 3. Pasos de instalación en una máquina nueva

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

## 4. Checklist de verificación

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

## 5. Sugerencias adicionales

- **Auto-crear la BD** (§2.1) elimina de raíz el incidente 503 de esta semana: la instalación pasa a ser "copiar, registrar, arrancar".
- **Rotar `Admin123!`** en producción: al estar el cambio de contraseña forzado (§2.2–2.3), el valor inicial deja de ser una exposición permanente.
- **Preservar las env vars al actualizar:** revisar el script del instalador (Inno Setup) y, si re-registra el servicio, que también ejecute el `nssm set AppEnvironmentExtra`.
- **Validar la config en el cliente:** que los mensajes de error de red distingan "servidor apagado", "credenciales de BD" y "versión incompatible" para facilitar el soporte en caja.
- **Acceso de dispositivos externos:** la regla de firewall queda limitada a la subred local. Si una caja está en otra VLAN/subred, añade su IP (`remoteip=<IP>`) en lugar de abrir la regla a todas las redes.
- **HTTPS real en el 5001:** el certificado actual es el de desarrollo; para clientes externos conviene instalar un certificado de confianza en el servidor o limitarse a HTTP (5000) dentro de la LAN.
- **Automatizar el firewall en el instalador:** igual que con las env vars, si el instalador se re-ejecuta, que también verifique/recree la regla de firewall (añadirla al script de Inno Setup o a un script de primer arranque).
