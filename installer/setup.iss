; Script Inno Setup para Sistema POS Administrador
#define MyAppName "Sistema POS Administrador"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Soluciones POS"
#define MyAppExeName "Desktop.Client.exe"

[Setup]
AppId={{C82F4D59-57C8-4A12-B603-7D1C2A59F890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\dist_installer
OutputBaseFilename=POS_System_Setup_v1.0.0
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Publicación Autónoma Backend API (.NET Self-Contained)
Source: "..\publish\BackendAPI\*"; DestDir: "{app}\BackendAPI"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.Production.json,appsettings.Development.json"
; Publicación Autónoma Cliente WPF Desktop (.NET Self-Contained)
Source: "..\publish\DesktopClient\*"; DestDir: "{app}\DesktopClient"; Flags: ignoreversion recursesubdirs createallsubdirs
; UpdaterService ejecutable
Source: "..\publish\UpdaterService\*"; DestDir: "{app}\UpdaterService"; Flags: ignoreversion recursesubdirs createallsubdirs
; NSSM ejecutable y Licencia (Opcional: Si está presente se empaqueta, si no se usa el fallback sc.exe)
#if FileExists("nssm.exe")
Source: "nssm.exe"; DestDir: "{app}\BackendAPI"; Flags: ignoreversion
#endif
#if FileExists("NSSM_LICENSE.txt")
Source: "NSSM_LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Dirs]
Name: "{commonappdata}\Registro de cierres"; Permissions: users-modify

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; El registro/actualización del servicio y la regla de firewall se gestionan en [Code] (ssPostInstall):
; - Guard de reinstalación: si PosBackendService ya existe se actualiza (binPath + AppEnvironmentExtra) en vez de fallar (INSTALLATION.md §5).
; - Los secretos (cadena de conexión y contraseña semilla) se aplican como AppEnvironmentExtra del servicio NSSM, no en appsettings (INSTALLATION.md §3.5, §2.5).
; - Firewall: solo HTTP 5000 a la subred local (INSTALLATION.md §3.2, §5).
; Iniciar Cliente Desktop al finalizar el Setup
Filename: "{app}\DesktopClient\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Detención y eliminación silenciosa del servicio de Windows al desinstalar (NSSM)
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "stop PosBackendService"; Flags: runhidden; Check: HasNssm; RunOnceId: "StopBackendServiceNssm"
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "remove PosBackendService confirm"; Flags: runhidden; Check: HasNssm; RunOnceId: "RemoveBackendServiceNssm"

; Detención y eliminación silenciosa del servicio de Windows al desinstalar (sc.exe)
Filename: "sc.exe"; Parameters: "stop PosBackendService"; Flags: runhidden; Check: NotHasNssm; RunOnceId: "StopBackendServiceSc"
Filename: "sc.exe"; Parameters: "delete PosBackendService"; Flags: runhidden; Check: NotHasNssm; RunOnceId: "DeleteBackendServiceSc"

[Code]
const
  ServiceName = 'PosBackendService';
  FirewallRuleHttp = 'Sistema POS - Backend API (TCP 5000)';
  FirewallRuleLegacy = 'Sistema POS - Backend API (TCP 5000/5001)';
  DefaultJwtSecretKey = 'ddf95c83c01224202681eee4525087512ece338e47f4c4897b6c5d72459b8795';

var
  DbPage: TInputQueryWizardPage;
  AdminPage: TInputQueryWizardPage;
  UpdatePage: TInputQueryWizardPage;

function HasNssm: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\BackendAPI\nssm.exe'));
end;

function NotHasNssm: Boolean;
begin
  Result := not HasNssm;
end;

function RunCmd(const Filename, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(Filename, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
end;

function ServiceExists: Boolean;
begin
  // sc query devuelve 0 si el servicio existe (aunque esté detenido); 1060 si no existe.
  Result := (RunCmd('sc.exe', 'query ' + ServiceName) = 0);
end;

procedure InitializeWizard;
begin
  // Página 1: Conexión PostgreSQL
  DbPage := CreateInputQueryPage(wpWelcome,
    'Configuración de Base de Datos PostgreSQL', 'Ingrese los datos de conexión a PostgreSQL',
    'Por favor especifique los parámetros del servidor de base de datos PostgreSQL.');
  DbPage.Add('Servidor Host:', False);
  DbPage.Add('Puerto:', False);
  DbPage.Add('Nombre de Base de Datos:', False);
  DbPage.Add('Usuario Postgres:', False);
  DbPage.Add('Contraseña Postgres:', True);

  DbPage.Values[0] := 'localhost';
  DbPage.Values[1] := '5432';
  DbPage.Values[2] := 'CommandCenterDb';
  DbPage.Values[3] := 'postgres';
  DbPage.Values[4] := 'postgres';

  // Página 2: Credenciales Semilla del Administrador
  AdminPage := CreateInputQueryPage(DbPage.ID,
    'Configuración Inicial de Administrador', 'Credenciales del primer usuario Administrador',
    'Especifique las credenciales para la cuenta de administración inicial del sistema.');
  AdminPage.Add('Usuario Administrador:', False);
  AdminPage.Add('Contraseña Administrador:', True);
  AdminPage.Add('Nombre del Negocio:', False);

  AdminPage.Values[0] := 'Admin';
  AdminPage.Values[1] := 'Admin123!';
  AdminPage.Values[2] := 'Mi Negocio POS';

  // Página 3: Servidor de Actualizaciones Centralizado
  UpdatePage := CreateInputQueryPage(AdminPage.ID,
    'Servidor de Actualizaciones Automáticas', 'Configuración de actualizaciones',
    'Ingrese la URL del servidor de parches y actualizaciones en la red.');
  UpdatePage.Add('URL Servidor de Actualizaciones:', False);
  UpdatePage.Values[0] := 'http://localhost:5000/updates/';
end;

// Escribe appsettings.Production.json SIN secretos cuando el servicio usa NSSM
// (la conexión y la contraseña semilla van como AppEnvironmentExtra del servicio).
// Solo el fallback sc.exe (que no puede fijar variables de entorno por servicio)
// incluye los secretos en el archivo, para que el backend pueda arrancar.
procedure WriteProductionConfig(IncludeSecrets: Boolean);
var
  ConfigFile, JsonContent, ConnString: String;
begin
  ConfigFile := ExpandConstant('{app}\BackendAPI\appsettings.Production.json');
  if FileExists(ConfigFile) then
    Exit; // Conserva configuraciones existentes en reinstalaciones/actualizaciones.

  if IncludeSecrets then
    ConnString := 'Host=' + DbPage.Values[0] + ';Port=' + DbPage.Values[1] + ';Database=' +
      DbPage.Values[2] + ';Username=' + DbPage.Values[3] + ';Password=' + DbPage.Values[4];

  JsonContent := '{' + #13#10;
  if IncludeSecrets then
    JsonContent := JsonContent +
      '  "ConnectionStrings": {' + #13#10 +
      '    "DefaultConnection": "' + ConnString + '"' + #13#10 +
      '  },' + #13#10;
  JsonContent := JsonContent +
    '  "SystemSettings": {' + #13#10 +
    '    "MinimumClientVersion": "1.0.0",' + #13#10 +
    '    "ServerVersion": "1.0.0",' + #13#10 +
    '    "UpdateServerUrl": "' + UpdatePage.Values[0] + '",' + #13#10 +
    '    "AdminSeedUsername": "' + AdminPage.Values[0] + '",' + #13#10;
  if IncludeSecrets then
    JsonContent := JsonContent +
      '    "AdminSeedPassword": "' + AdminPage.Values[1] + '",' + #13#10;
  JsonContent := JsonContent +
    '    "BusinessName": "' + AdminPage.Values[2] + '"' + #13#10 +
    '  }';
  if IncludeSecrets then
    JsonContent := JsonContent + ',' + #13#10 +
      '  "JwtSettings": {' + #13#10 +
      '    "Key": "' + DefaultJwtSecretKey + '"' + #13#10 +
      '  }';
  JsonContent := JsonContent + #13#10 + '}';

  SaveStringToFile(ConfigFile, JsonContent, False);
end;

// Regla de firewall idempotente: expone SOLO HTTP 5000 a la subred local.
// El puerto 5001 (HTTPS) usa un certificado autofirmado que los clientes de la LAN no pueden
// validar (INSTALLATION.md §3.2 y §5); por eso se deshabilita en la regla y se fuerza HTTP en la red.
procedure ConfigureFirewall;
var
  Code: Integer;
begin
  // Retira la regla antigua (5000/5001) si existe: idempotente y elimina la exposición del 5001.
  RunCmd('netsh.exe', 'advfirewall firewall delete rule name="' + FirewallRuleLegacy + '"');

  Code := RunCmd('netsh.exe', 'advfirewall firewall add rule name="' + FirewallRuleHttp +
    '" dir=in action=allow protocol=TCP localport=5000 remoteip=localsubnet profile=any');
  if Code <> 0 then
    MsgBox('No se pudo crear la regla de Firewall de Windows para el puerto 5000.' + #13#10 + #13#10 +
      'Créela manualmente en una consola elevada:' + #13#10 +
      'netsh advfirewall firewall add rule name="' + FirewallRuleHttp +
      '" dir=in action=allow protocol=TCP localport=5000 remoteip=localsubnet profile=any',
      mbError, MB_OK);
end;

// Registra o actualiza el servicio PosBackendService sin fallar en reinstalaciones,
// y reaplica siempre las variables de entorno (ConnectionStrings__DefaultConnection,
// SystemSettings__AdminSeedPassword y JWT_SETTINGS_KEY) vía AppEnvironmentExtra de NSSM (INSTALLATION.md §3.5, §5 y JWT_Key.md).
procedure RegisterOrUpdateService(UseNssm: Boolean);
var
  AppExe, AppDir, ConnEnv, SeedEnv, JwtEnv, ConnString: String;
  Code: Integer;
begin
  AppExe := ExpandConstant('{app}\BackendAPI\Backend.API.exe');
  AppDir := ExpandConstant('{app}\BackendAPI');
  ConnString := 'Host=' + DbPage.Values[0] + ';Port=' + DbPage.Values[1] + ';Database=' +
    DbPage.Values[2] + ';Username=' + DbPage.Values[3] + ';Password=' + DbPage.Values[4];
  ConnEnv := 'ConnectionStrings__DefaultConnection=' + ConnString;
  SeedEnv := 'SystemSettings__AdminSeedPassword=' + AdminPage.Values[1];
  JwtEnv := 'JWT_SETTINGS_KEY=' + DefaultJwtSecretKey;

  if UseNssm then
  begin
    if ServiceExists then
    begin
      // Actualización: actualiza binPath, AppDirectory y reaplica env vars, luego reinicia.
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Application "' + AppExe + '"');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppDirectory "' + AppDir + '"');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Start SERVICE_AUTO_START');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppEnvironmentExtra "' + ConnEnv + '" "' + SeedEnv + '" "' + JwtEnv + '"');
      Code := RunCmd(AppDir + '\nssm.exe', 'restart ' + ServiceName);
    end
    else
    begin
      Code := RunCmd(AppDir + '\nssm.exe', 'install ' + ServiceName + ' "' + AppExe + '"');
      if Code = 0 then
      begin
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppDirectory "' + AppDir + '"');
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Start SERVICE_AUTO_START');
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppEnvironmentExtra "' + ConnEnv + '" "' + SeedEnv + '" "' + JwtEnv + '"');
        Code := RunCmd(AppDir + '\nssm.exe', 'start ' + ServiceName);
      end;
    end;
  end
  else
  begin
    // Fallback sin NSSM: sc.exe no puede fijar variables de entorno por servicio,
    // por lo que los secretos quedan en appsettings.Production.json (WriteProductionConfig(True)).
    if ServiceExists then
    begin
      RunCmd('sc.exe', 'stop ' + ServiceName);
      Code := RunCmd('sc.exe', 'config ' + ServiceName + ' binPath= "' + AppExe + '" start= auto');
      if Code = 0 then
        Code := RunCmd('sc.exe', 'start ' + ServiceName);
    end
    else
    begin
      Code := RunCmd('sc.exe', 'create ' + ServiceName + ' binPath= "' + AppExe + '" start= auto');
      if Code = 0 then
        Code := RunCmd('sc.exe', 'start ' + ServiceName);
    end;
  end;

  if Code <> 0 then
    MsgBox('No se pudo iniciar el servicio ' + ServiceName + '. Revise los registros en ' +
      AppDir + '\logs para más detalles.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // 1) Config de producción sin secretos (con NSSM); con secretos solo en el fallback sc.exe.
    WriteProductionConfig(not HasNssm);
    // 2) Firewall: solo HTTP 5000 a la subred local.
    ConfigureFirewall;
    // 3) Registrar/actualizar el servicio conservando y reaplicando las env vars.
    RegisterOrUpdateService(HasNssm);
  end;
end;
