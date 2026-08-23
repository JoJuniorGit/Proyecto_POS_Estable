# JWT Secret Key -- Variable de Entorno

El backend requiere una **JWT Secret Key** de al menos 32 caracteres para firmar tokens de autenticacion. En produccion, esta clave **no debe ir en texto plano dentro de appsettings.json**; se registra como **variable de entorno del servicio Windows**.

---

## Clave actual

    JWT_SETTINGS_KEY=ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795

> Esta clave fue generada aleatoriamente con openssl rand -hex 32 para este despliegue.
> Si el repositorio fuente tiene su propia clave, reemplazala.

---

## Registro manual (NSSM)

Si el servicio PosBackendService ya esta registrado con NSSM, anyade la variable de entorno asi:

    "C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe" set PosBackendService AppEnvironmentExtra JWT_SETTINGS_KEY=ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795

> **Importante:** Si el servicio ya tiene otras variables de entorno registradas (ConnectionStrings__DefaultConnection, etc.), NSSM **sobrescribe** la lista completa. En ese caso, registra todas las variables juntas separadas por \0:

    "C:\Program Files (x86)\Sistema POS Administrador\BackendAPI\nssm.exe" set PosBackendService AppEnvironmentExtra ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=POSTGRES\0SystemSettings__AdminSeedPassword=Admin123!\0JWT_SETTINGS_KEY=ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795

Despues reinicia el servicio:

    net stop PosBackendService && net start PosBackendService

---

## Registro en el instalador (Inno Setup)

Anyade estos pasos al script [Code] del instalador para que registre las variables de entorno **cada vez que se instala o actualiza**:

    [Code]
    procedure RegisterServiceEnvironment;
    var
      ResultCode: Integer;
      EnvVars: String;
    begin
      EnvVars :=
        'ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=POSTGRES'
        + #0 +
        'SystemSettings__AdminSeedPassword=Admin123!'
        + #0 +
        'JWT_SETTINGS_KEY=ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795';

      Exec(
        ExpandConstant('{app}\BackendAPI\nssm.exe'),
        'set PosBackendService AppEnvironmentExtra ' + EnvVars,
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode
      );

      Exec(
        'cmd.exe',
        '/c net stop PosBackendService && timeout /t 3 /nobreak >/dev/null && net start PosBackendService',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode
      );
    end;

    procedure CurStepChanged(CurStep: TSetupStep);
    begin
      if CurStep = ssPostInstall then
      begin
        RegisterServiceEnvironment;
      end;
    end;

> **Nota:** Las contrasenas estan en claro en el script del instalador. Para produccion, considere usar variables de entorno del sistema (no del servicio) o un secret manager.

---

## Registro manual via Editor del Registro

Si NSSM no esta disponible, edita directamente el registro:

1. Abre egedit
2. Navega a: HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PosBackendService\Parameters
3. Crea o edita el valor AppEnvironmentExtra (tipo REG_MULTI_SZ)
4. Anyade cada variable en una linea separada:

       ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=CommandCenterDb;Username=postgres;Password=POSTGRES
       SystemSettings__AdminSeedPassword=Admin123!
       JWT_SETTINGS_KEY=ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795

5. Reinicia el servicio: 
et stop PosBackendService && net start PosBackendService

---

## Variables de entorno requeridas (resumen)

| Variable | Descripcion | Requerida |
|---|---|---|
| ConnectionStrings__DefaultConnection | Cadena de conexion a PostgreSQL | Si |
| JWT_SETTINGS_KEY | Clave secreta JWT (>=32 chars) | Si |
| SystemSettings__AdminSeedPassword | Contrasena del admin semilla | Si |

---

## Verificacion

Tras registrar las variables y reiniciar, verifica con:

    REM Verificar que el servicio escucha
    netstat -ano | findstr ":5000.*LISTEN"

    REM Verificar health
    curl http://localhost:5000/health

    REM Verificar login
    curl -X POST http://localhost:5000/api/auth/login -H "Content-Type: application/json" -d "{\"cedula\":\"12345678\",\"password\":\"Admin123!\"}"

Si el login devuelve un JWT token, todo funciona correctamente.

---

## Solucion de problemas

### El servicio no arranca tras registrar las variables

1. Revisa el log: BackendAPI\logs\crash.log
2. Busca el mensaje: CRITICAL: JWT Secret Key -- significa que la variable no se detecto
3. Verifica que AppEnvironmentExtra este bien formateado (cada variable en linea separada, separadas por \0)

### El servicio arranca pero el login falla con error 500

1. Verifica que la JWT key tenga **al menos 32 caracteres**
2. Reinicia el servicio para que tome la nueva configuracion