# Guía de Instalación — Sistema POS CommandCenter

Guía básica para instalación, configuración y verificación del sistema.

---

## 1. Estado del Despliegue

| Elemento | Estado |
|---|---|
| Base de datos `CommandCenterDb` | Creada en PostgreSQL 18 local |
| Migraciones EF Core | Aplicadas |
| Datos semilla | Sembrados |
| Secretos | En `BackendAPI\secrets.json` (ACL protegido) |
| Certificado HTTPS | Autofirmado generado en el puesto |
| Backup PostgreSQL | Tarea programada 03:00 (activa por defecto) |
| Firewall | Regla TCP 5000/5001 (subred local) |

---

## 2. Seguridad Implementada

- **Arranque robusto fail-fast + auto-creación de BD** (`Program.cs`)
- **Seed con `MustChangePassword`**: admin semilla marcado con cambio obligatorio
- **Login con cambio obligatorio**: `403` con `requiresPasswordChange`
- **Secretos en `secrets.json`**: no en variables de entorno ni `appsettings`

---

## 3. Convención de Configuración

Fusión (orden de precedencia):
1. `appsettings.json` — compartido, sin secretos
2. `appsettings.{Environment}.json` — overrides por entorno
3. `secrets.json` — únicamente secretos (máxima precedencia)

### Qué va en cada archivo

| Archivo | Contenido | Secretos |
|---|---|---|
| `appsettings.json` | Logging, AllowedHosts, MinimumClientVersion, ServerVersion | **Ninguno** |
| `appsettings.Production.json` | AdminSeedUsername, BusinessName | **Ninguno** |
| `secrets.json` | ConnectionStrings, AdminSeedPassword, JwtSettings.Key, Kestrel cert | **Sí** — ACL |

### Formato de `secrets.json`

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

---

## 4. Pasos de Instalación

1. **Prerrequisitos:** PostgreSQL 18 con usuario `postgres`; runtime .NET compatible.
2. **Abrir puertos en firewall:**
   ```bat
   netsh advfirewall firewall add rule name="Sistema POS - Backend API (TCP 5000/5001)" dir=in action=allow protocol=TCP localport=5000,5001 remoteip=localsubnet profile=any
   ```
3. **Copiar binarios** (BackendAPI, DesktopClient, UpdaterService).
4. **Registrar servicio** con NSSM:
   ```bat
   nssm install PosBackendService "C:\...\BackendAPI\Backend.API.exe"
   nssm set PosBackendService AppDirectory "C:\...\BackendAPI"
   ```
5. **Crear `BackendAPI\secrets.json`** (el instalador lo genera automáticamente).
6. **Generar certificado HTTPS** (si se usa puerto 5001):
   ```powershell
   .\scripts\create-https-cert.ps1 -Subject "CN=<nombre-del-equipo>" -SecretsFile "C:\...\BackendAPI\secrets.json"
   ```
7. **Arrancar:**
   ```bat
   sc start PosBackendService
   ```
8. **Instalar cliente** en equipos de caja apuntando al servidor.

---

## 5. Checklist de Verificación

| # | Prueba | Esperado |
|---|---|---|
| 1 | `curl http://localhost:5000/health` | `200` con `"database":"Connected"` |
| 2 | `curl "http://localhost:5000/api/products?page=1&pageSize=5"` | `200` con items |
| 3 | `curl http://localhost:5000/api/PaymentMethods/active` | `200` con Cash/Card |
| 4 | Login con admin semilla | `403` con `requiresPasswordChange` |
| 5 | Cambio de contraseña + nuevo login | `200` y acceso normal |
| 6 | `BackendAPI/logs/start.log` | "Database Connection successful" |

### Paridad instalador ↔ backend

| Regla | Instalador | Backend |
|---|---|---|
| Longitud mínima | `>= 4` | `>= 4` |
| Composición | 1 letra + 1 número | 1 letra + 1 número |
| Blacklist de comunes | no validada | sí (28 entradas) |

---

## Archivos Relacionados

- `INSTALLATION-advanced.md` — Backup, runbooks, prueba de estrés, piloto
- `docs/LAUNCH_GUIDE.md` — Monitoreo externo (Prometheus, Grafana, UptimeRobot)
- `architecture-core.md` — Arquitectura general del sistema
