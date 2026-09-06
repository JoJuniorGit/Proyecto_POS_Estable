# Documentación Técnica del Instalador — Sistema POS Administrador

Este documento describe la arquitectura, proceso de compilación, políticas de configuración y resolución de problemas del instalador Inno Setup del Sistema POS Administrador.

---

## 1. Compilación del Instalador

### Requisitos Previos
1. **Inno Setup 6:** Descargar e instalar desde [jrsoftware.org](https://jrsoftware.org/isdl.php).
2. **Binarios Autónomos Publicados:** Antes de compilar el instalador, los tres proyectos deben haber sido publicados en la carpeta `publish/`:
   ```powershell
   # Desde la raíz del repositorio
   dotnet publish Backend.API/Backend.API.csproj -c Release -r win-x64 --self-contained -o publish/BackendAPI
   dotnet publish Desktop.Client/Desktop.Client.csproj -c Release -r win-x64 --self-contained -o publish/DesktopClient
   ```
3. **NSSM:** Debe existir `installer/nssm.exe` (Non-Sucking Service Manager v2.24 o superior). Si no existe, ejecute:
   ```powershell
   powershell -ExecutionPolicy Bypass -File installer/download-nssm.ps1
   ```

### Comando de Compilación
Ejecutar el compilador de Inno Setup desde la raíz del proyecto:
```cmd
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\setup.iss
```
El instalador generado se ubicará en:
`dist_installer\POS_System_Setup_v1.0.0.exe`

---

## 2. Arquitectura de Despliegue y Auto-Recuperación

El instalador delega la configuración del sistema en el script elevado e idempotente:
`{app}\tools\Configure-PosService.ps1`

### 2.1 Reglas de Firewall (Idempotentes)
- Se eliminan previamente todas las reglas duplicadas o residuales que coincidan con `Sistema POS - Backend API*`.
- Se abren dos reglas entrantes exclusivas para la subred local (`remoteip=localsubnet`, `profile=any`):
  1. **TCP 5000:** Servicio HTTP (API REST, Web POS, SignalR `/hubs/exchange-rate`).
  2. **TCP 5001:** Servicio HTTPS (Requerido por navegadores móviles para el uso de la cámara/escáner de código de barras).

### 2.2 Política de Fusión de Variables de Entorno (NSSM `AppEnvironmentExtra`)
NSSM almacena las variables de entorno como un array `REG_MULTI_SZ` en el registro de Windows (`HKLM\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters\AppEnvironmentExtra`).

Al actualizar o reinstalar:
1. **Variables Sensibles (`SystemSettings__AdminSeedPassword`):** **NO se sobrescriben** si ya existen en el servicio. Esto garantiza que si el administrador ya cambió su contraseña en producción, una actualización de software no revierta su credencial a la contraseña inicial por defecto.
2. **Variables Criptográficas (`JWT_SETTINGS_KEY`):** **Se conservan** las existentes para evitar la invalidación de tokens y desconexión abrupta de clientes activos, a menos que se invoque explícitamente con `-RotateJwt`.
3. **Variables de Conexión y Negocio:** Se actualizan con los valores introducidos en el instalador (`ConnectionStrings__DefaultConnection`, `SystemSettings__*`), manteniendo cualquier otra clave personalizada agregada previamente por el usuario.
4. **Variables Nuevas:** Se integran automáticamente.

### 2.3 Jerarquía de Precedencia de Configuración
1. **Variables de entorno del servicio (`AppEnvironmentExtra` de NSSM):** Máxima prioridad.
2. **`appsettings.Production.json`:** Respaldo estático en disco. `Configure-PosService.ps1` **no sobreescribe** este archivo para no perder modificaciones manuales.
3. **`appsettings.json`:** Valores predeterminados del ensamblado base.

---

## 3. Certificado Autofirmado para HTTPS (Puerto 5001)

El backend soporta HTTPS automáticamente si detecta el certificado `certs/pos-https.pfx` en el directorio de la aplicación (`{app}\BackendAPI\certs\pos-https.pfx`).

Para generar o renovar este certificado:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/create-https-cert.ps1
```
El certificado se genera con una validez de 10 años e incluye las direcciones IPv4 de la red local en sus Subject Alternative Names (SANs).

---

## 4. Diagnóstico y Registros de Instalación

Si ocurre alguna anomalía durante la instalación, consulte los archivos en:
`C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\logs\`

- **`installer.log`:** Registro de ejecución de `Configure-PosService.ps1` (verificación de NSSM, reglas de firewall, variables fusionadas, reintentos de arranque del servicio).
- **`start.log`:** Registro de inicialización del proceso `Backend.API.exe`.
- **`crash.log`:** Excepciones no controladas globales del backend.
- **`db-errors.log`:** Fallos de conectividad, autenticación o migraciones de PostgreSQL.

---

## 5. Lista de Verificación de Validación

Tras una instalación o actualización, verifique en una consola elevada de PowerShell:
```powershell
# 1. Firewall: exactamente 2 reglas, ambas para la subred local
Get-NetFirewallRule -DisplayName "Sistema POS - Backend API*" | Select-Object DisplayName, Enabled, Action, Direction

# 2. Variables de NSSM persistidas correctamente
& "C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe" get PosBackendService AppEnvironmentExtra

# 3. Estado del servicio
Get-Service PosBackendService

# 4. Salud del backend (debe retornar 200 OK)
Invoke-RestMethod http://localhost:5000/api/settings/timezone
```
