# Guía Avanzada — Backup, Runbooks, Estrés y Piloto

> Operaciones avanzadas: backup/restore, runbooks de emergencia, prueba de estrés y plan del piloto.

---

## 6. Sugerencias Adicionales

- **Auto-crear la BD** elimina incidentes 503: la instalación pasa a ser "copiar, registrar, arrancar".
- **Rotar `Admin123!`** en producción: el cambio de contraseña forzado cubre la exposición inicial.
- **Acceso de dispositivos externos:** limitar `remoteip` a IP concreta en lugar de `localsubnet`.
- **HTTPS real:** importar `.cer` en almacén raíz de confianza de cada caja.

### Windows Defender / SmartScreen

1. Desbloquear con `Unblock-File` si los binarios llegan descargados.
2. Excluir procesos específicos en Microsoft Defender (recomendado sobre exclusión de carpetas):
   ```powershell
   Add-MpPreference -ExclusionProcess "Backend.API.exe"
   Add-MpPreference -ExclusionProcess "UpdaterService.exe"
   Add-MpPreference -ExclusionProcess "nssm.exe"
   ```

---

## 7. Operación sin Conexión (Tasa BCV Manual)

Si el puesto queda sin red:

1. **Desactivar auto-sync:** `BcvSettings:AutoSyncIntervalMinutes` = `0`
2. **Ingresar tasa manualmente:**
   ```http
   POST http://localhost:5000/api/exchange-rate
   Authorization: Bearer <token-admin>
   Content-Type: application/json
   { "value": 73.25 }
   ```
3. Si la red se recupera: `POST /api/exchange-rate/sync-bcv` o reactivar auto-sync.
4. **Integridad:** la tasa del día queda como `AppliedRate` persistida; el historial nunca se recalcula.

---

## 8. Backup Automático de PostgreSQL

Tarea programada `Sistema POS - Backup PostgreSQL` (diaria 03:00):

- Volcado `custom` (`pg_dump -Fc`) con compresión máxima
- Retención de 14 días
- Credenciales desde `secrets.json` (nunca por CLI)

### Restauración

```powershell
# 1) Identificar volcado más reciente
$backup = Get-ChildItem "C:\Backups\CommandCenter\*.dump" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

# 2) Crear BD si no existe
& "C:\Program Files\PostgreSQL\16\bin\createdb.exe" --username "postgres" --host localhost "commandcenter"

# 3) Restaurar
& "C:\Program Files\PostgreSQL\16\bin\pg_restore.exe" --verbose --clean --if-exists --no-owner `
    --username "postgres" --host localhost --dbname "commandcenter" $backup.FullName

# 4) Verificar
Invoke-RestMethod "http://localhost:5000/health"
```

### Respaldo de Configuración del Sitio

| Elemento | Ruta |
|---|---|
| `secrets.json` | `BackendAPI\secrets.json` |
| Certificado HTTPS | `BackendAPI\certs` (PFX + `.cer`) |
| `client_settings.json` | Carpeta del cliente en cada caja |

---

## 9. Limitaciones Pre-Piloto

- **Impresión:** recibos como PDF (no fiscal), sin impresora térmica física
- **UpdaterService:** no incluido en instalador (firma X.509 pendiente)
- **Multi-sucursal:** sin `BranchId` (intención futura)
- **Certificado HTTPS:** autofirmado, clientes ven advertencia la primera vez

---

## 10. Plan de Actualización y Rollback

### Actualización (upgrade)

1. Respaldar (`backup-postgres.ps1`)
2. Publicar nuevo Release y copiar al puesto
3. Detener servicio (`Stop-Service "PosBackendService"`)
4. Reemplazar binarios preservando `secrets.json`
5. Iniciar servicio (ejecuta `MigrateAsync()`)
6. Verificar con `/health` y smoke `MigratedSchema`

### Rollback

- **Sin cambio de esquema:** reemplazar binarios por versión anterior
- **Esquema aditivo:** degradar binarios (base compatible hacia atrás)
- **Revertir datos:** restaurar volcado previo (`pg_restore`)

---

## 11. Matriz de Configuración por Ambiente

| Parámetro | Desarrollo | QA | Producción |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | Development | Production | Production |
| `ConnectionStrings` | local dev | BD QA | secrets.json |
| `AdminSeedPassword` | User Secrets | CSPRNG por sitio | CSPRNG por sitio |
| `JwtSettings.Key` | clave dev | CSPRNG 512 bits | CSPRNG 512 bits |
| `RequireHttpsMetadata` | false | false (LAN) | true si no es LAN |
| `Idempotency:RequireCashSessionAndInvoiceStage` | false (compatibilidad) | false hasta actualizar clientes | true tras el rollout |

### Secretos del entorno de desarrollo

`Backend.API/appsettings.Development.json` no debe almacenar credenciales. En Desarrollo, ASP.NET Core carga User Secrets automáticamente mediante el `UserSecretsId` del proyecto:

```powershell
dotnet user-secrets list --project Backend.API/Backend.API.csproj
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Database=CommandCenterDb;Username=postgres;Password=<secreto>" --project Backend.API/Backend.API.csproj
dotnet user-secrets set "SystemSettings:AdminSeedPassword" "<secreto aleatorio>" --project Backend.API/Backend.API.csproj
```

El almacén es local al perfil de Windows y no se publica ni se versiona. El secreto de seed solo se usa al crear una instalación nueva; cambiarlo no rota automáticamente la contraseña de un administrador que ya existe. Para Producción, conservar `secrets.json` protegido por ACL o usar el almacén seguro del sitio. El desktop usa HTTPS y pinning TOFU por host privado; para Web/móviles, el certificado debe ser confiable para el navegador o instalarse mediante el procedimiento del sitio.

### Rollout de claves de idempotencia

Las versiones actuales de WPF y Web envían `Idempotency-Key` para apertura/cierre de caja y staging de facturas proveedor. El servidor registra temporalmente las solicitudes sin clave para identificar clientes legacy, pero las acepta mientras `Idempotency:RequireCashSessionAndInvoiceStage` sea `false`. Después de actualizar toda la flota, establecerlo en `true` en `BackendAPI/secrets.json`; a partir de ahí los requests sin clave reciben `400`.

---

## 12. Checklist de Instalación por Cliente

1. PostgreSQL 16/18 instalado con usuario `postgres` y `CREATEDB`
2. Puertos 5000/5001 abiertos en firewall
3. Ejecutar instalador como Administrador
4. Verificar servicio NSSM, firewall, `secrets.json` con ACL, certificado, backup
5. Probar `GET /health` y smoke `MigratedSchema`
6. Verificar el certificado HTTPS y su confianza en desktop y navegador/móviles; la primera conexión desktop a un certificado local queda fijada con DPAPI.
7. Cambio obligatorio de contraseña del admin

---

## 13. Runbooks Operativos

### 13.1 Servicio backend caído
1. `Get-Service "PosBackendService"` → si `Stopped`, `Start-Service`
2. Revisar `logs\crash.log` y `logs\start.log`
3. Validar `secrets.json` y que PostgreSQL esté arriba

### 13.2 Certificado HTTPS inválido
1. `Configure-PosService.ps1` regenera el PFX
2. Verificar que `secrets.json` casa con el PFX
3. Reiniciar servicio

### 13.3 Tasa BCV no disponible
1. Fijar `AutoSyncIntervalMinutes` ≤ 0
2. Registrar tasa manual: `POST /api/exchange-rate { "value": <tasa> }`

### 13.4 Stock inconsistente
1. Revisar `StockMovements` y historial de venta
2. Ajustar manualmente con permiso Admin (`AdjustStock`)

### 13.5 Restore / rollback
- Restore: §8.1 (`pg_restore --clean --if-exists --no-owner`)
- Rollback: §10 (binarios previos o restore del volcado)

### 13.6 Backup fallando
1. Verificar tarea programada y ejecutar `backup-postgres.ps1` manualmente
2. Comprobar espacio en disco y credenciales

---

## 14. Plan del Piloto Controlado

### Alcance
- **Versión congelada:** V0.15 en UNA sucursal
- **Duración:** 1-2 semanas
- **SLOs:** RPO ≤ 24h, RTO ≤ 4h, checkout sin duplicados

### Registro Diario

| Día | Ventas | Cierres | Errores | Latencia | Backup OK | Intervenciones |
|-----|--------|---------|---------|----------|-----------|----------------|
|     |        |         |         |          |           |                |

### Criterios de Aceptación (M6)

- [ ] Cero incidentes críticos y cero pérdida de datos
- [ ] Operación dentro de los SLOs
- [ ] Backup diario OK y al menos un restore de validación
- [ ] Sin duplicados de venta/abono bajo reintentos
- [ ] Aceptación formal del cliente
