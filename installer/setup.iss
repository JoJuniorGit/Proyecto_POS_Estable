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
SetupIconFile=app.ico
UninstallIconFile=app.ico

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Publicación Autónoma Backend API (.NET Self-Contained)
; 8.27-A01/A04: se excluyen appsettings de entorno y el pfx stale del puesto de build;
; el certificado HTTPS se genera en la MÁQUINA DESTINO (tools\create-https-cert.ps1) para
; que sus SANs correspondan al cliente y nunca viajen credenciales de desarrollo.
Source: "..\publish\BackendAPI\*"; DestDir: "{app}\BackendAPI"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "appsettings.Production.json,appsettings.Development.json,certs\pos-https.pfx"
; Publicación Autónoma Cliente WPF Desktop (.NET Self-Contained)
Source: "..\publish\DesktopClient\*"; DestDir: "{app}\DesktopClient"; Flags: ignoreversion recursesubdirs createallsubdirs
; NSSM ejecutable y Licencia (Opcional: Si está presente se empaqueta, si no se usa el fallback sc.exe)
#if FileExists("nssm.exe")
Source: "nssm.exe"; DestDir: "{app}\BackendAPI"; Flags: ignoreversion
#endif
#if FileExists("NSSM_LICENSE.txt")
Source: "NSSM_LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
#endif
; Script PowerShell de Configuración Idempotente (Firewall, NSSM, Env Vars)
Source: "Configure-PosService.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
; 8.27-A02/A4: scripts de operación desplegados al puesto: certificado HTTPS por sitio y backup PostgreSQL
Source: "..\scripts\create-https-cert.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\scripts\backup-postgres.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion


[Dirs]
Name: "{commonappdata}\Registro de cierres"; Permissions: users-modify

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; El registro/actualización del servicio y la regla de firewall se gestionan en [Code] (ssPostInstall):
; - Guard de reinstalación: si PosBackendService ya existe se actualiza (binPath) en vez de fallar (INSTALLATION.md §5).
; - 8.29-A1: los secretos (cadena de conexión, contraseña semilla, clave JWT y contraseña del
;   certificado) residen ÚNICAMENTE en BackendAPI\secrets.json con ACL restrictiva; NO viajan
;   por argv ni AppEnvironmentExtra del servicio NSSM (INSTALLATION.md §2.5, §3.5).
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

var
  DbPage: TInputQueryWizardPage;
  AdminPage: TInputQueryWizardPage;
  UpdatePage: TInputQueryWizardPage;

procedure WriteProtectedSecretsFile(AppDir: String; ConnString, JwtKey, CertPass, SeedPass: String); forward;

function JsonEsc(const S: String): String;
var
  i: Integer;
  c: Char;
begin
  Result := '';
  for i := 1 to Length(S) do
  begin
    c := S[i];
    if c = '\' then Result := Result + '\\'
    else if c = '"' then Result := Result + '\"'
    else Result := Result + c;
  end;
end;

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

// 8.29-A1: genera un secreto criptográfico con CSPRNG (RandomNumberGenerator) vía
// PowerShell, escribiéndolo en un archivo temporal (NUNCA por línea de comandos).
// Devuelve cadena hexadecimal (Bytes*2 chars) o '' si falla.
function GenerateCryptoSecret(Bytes: Integer): String;
var
  OutFile, PS: String;
  Raw: AnsiString;
  ResCode: Integer;
begin
  Result := '';
  OutFile := GetTempDir + '\pos-csp.tmp';
  DeleteFile(OutFile);
  PS := '-NoProfile -ExecutionPolicy Bypass -Command "$b = New-Object byte[] ' + IntToStr(Bytes) +
    '; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); ' +
    '[System.IO.File]::WriteAllText(''' + OutFile + ''', ' +
    '[System.BitConverter]::ToString($b).Replace('' '','''').Replace(''-'','''').ToLower())"';
  try
    if Exec('powershell.exe', PS, '', SW_HIDE, ewWaitUntilTerminated, ResCode) and (ResCode = 0) and
       LoadStringFromFile(OutFile, Raw) and (Length(Raw) > 0) then
    begin
      Result := String(Raw);
      DeleteFile(OutFile);
      Exit;
    end;
  finally
    if FileExists(OutFile) then
      DeleteFile(OutFile);
  end;
end;

// 8.29-A1 (revisión reviewer): la clave JWT y la password del certificado se generan
// por sitio pero NO se persisten en el registro (HKLM\Software\POS queda limpio). Solo
// viajan en memoria a WriteProtectedSecretsFile -> secrets.json (única fuente veraz con
// ACL). En reinstalaciones se preservan desde secrets.json (Configure-PosService.ps1).
function GetOrGenerateJwtKey: String;
begin
  Result := GenerateCryptoSecret(32);
end;

function GetOrGenerateCertPass: String;
begin
  Result := GenerateCryptoSecret(16);
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
  DbPage.Values[4] := '';

  // Página 2: Credenciales Semilla del Administrador
  AdminPage := CreateInputQueryPage(DbPage.ID,
    'Configuración Inicial de Administrador', 'Credenciales del primer usuario Administrador',
    'Defina las credenciales de acceso para la cuenta de administración inicial del sistema.' + #13#10 +
    'El usuario podrá iniciar sesión inmediatamente con estas credenciales.');
  AdminPage.Add('Usuario Administrador (ej: admin, gerente):', False);
  AdminPage.Add('Nombre Completo / Personal:', False);
  AdminPage.Add('Contraseña Administrador:', True);
  AdminPage.Add('Confirmar Contraseña:', True);
  AdminPage.Add('Nombre del Negocio:', False);

  AdminPage.Values[0] := 'admin';
  AdminPage.Values[1] := 'Administrador Principal';
  AdminPage.Values[2] := '';
  AdminPage.Values[3] := '';
  AdminPage.Values[4] := 'Mi Negocio POS';

  // Página 3: Servidor de Actualizaciones Centralizado
  UpdatePage := CreateInputQueryPage(AdminPage.ID,
    'Servidor de Actualizaciones Automáticas', 'Configuración de actualizaciones',
    'Ingrese la URL del servidor de parches y actualizaciones en la red.');
  UpdatePage.Add('URL Servidor de Actualizaciones:', False);
  UpdatePage.Values[0] := 'https://localhost:5001/updates/';
end;

// Validación de la página AdminPage antes de avanzar al siguiente paso.
function PasswordHasLetterAndDigit(const Value: String): Boolean;
var
  i: Integer;
  HasLetter, HasDigit: Boolean;
  c: Char;
begin
  HasLetter := False;
  HasDigit := False;
  for i := 1 to Length(Value) do
  begin
    c := Value[i];
    if ((c >= 'A') and (c <= 'Z')) or ((c >= 'a') and (c <= 'z')) then
      HasLetter := True
    else if (c >= '0') and (c <= '9') then
      HasDigit := True;
  end;
  Result := HasLetter and HasDigit;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Username, Password, Confirm: String;
begin
  Result := True;

  if CurPageID = AdminPage.ID then
  begin
    Username := Trim(AdminPage.Values[0]);
    Password := AdminPage.Values[2];
    Confirm  := AdminPage.Values[3];

    if Username = '' then
    begin
      MsgBox('El campo "Usuario Administrador" es obligatorio. Por favor ingrese un nombre de usuario.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if Length(Password) < 4 then
    begin
      MsgBox('La contraseña debe tener al menos 4 caracteres.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not PasswordHasLetterAndDigit(Password) then
    begin
      MsgBox('La contraseña debe contener al menos 1 letra y 1 número.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if Password <> Confirm then
    begin
      MsgBox('Las contraseñas no coinciden. Por favor verifique que ambos campos sean idénticos.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

// 8.29-A1: escribe appsettings.Production.json SIN secretos. Los secretos (cadena de
// conexión, contraseña semilla, clave JWT y contraseña del certificado) viven
// ÚNICAMENTE en secrets.json (WriteProtectedSecretsFile), que el backend carga con
// máxima precedencia, tanto en el path NSSM como en el fallback sc.exe.
procedure WriteProductionConfig;
var
  ConfigFile, JsonContent: String;
begin
  ConfigFile := ExpandConstant('{app}\BackendAPI\appsettings.Production.json');
  if FileExists(ConfigFile) then
    Exit; // Conserva configuraciones existentes en reinstalaciones/actualizaciones.

  // Nota: AdminPage.Values[0]=Username, [1]=FullName, [2]=Password, [3]=Confirm, [4]=BusinessName
  JsonContent := '{' + #13#10 +
    '  "SystemSettings": {' + #13#10 +
    '    "MinimumClientVersion": "1.0.0",' + #13#10 +
    '    "ServerVersion": "1.0.0",' + #13#10 +
    '    "UpdateServerUrl": "' + UpdatePage.Values[0] + '",' + #13#10 +
    '    "AdminSeedUsername": "' + Trim(AdminPage.Values[0]) + '",' + #13#10 +
    '    "AdminSeedName": "' + Trim(AdminPage.Values[1]) + '",' + #13#10 +
    '    "BusinessName": "' + AdminPage.Values[4] + '"' + #13#10 +
    '  }' + #13#10 +
    '}';

  SaveStringToFile(ConfigFile, JsonContent, False);
end;

// Regla de firewall idempotente: expone HTTP 5000 y HTTPS 5001 a la subred local.
procedure ConfigureFirewall;
var
  Code: Integer;
begin
  // Retira reglas antiguas si existen
  RunCmd('netsh.exe', 'advfirewall firewall delete rule name="' + FirewallRuleLegacy + '"');
  RunCmd('netsh.exe', 'advfirewall firewall delete rule name="' + FirewallRuleHttp + '"');

  Code := RunCmd('netsh.exe', 'advfirewall firewall add rule name="' + FirewallRuleLegacy +
    '" dir=in action=allow protocol=TCP localport=5000,5001 remoteip=localsubnet profile=any');
  if Code <> 0 then
    MsgBox('No se pudo crear la regla de Firewall de Windows para los puertos 5000 y 5001.' + #13#10 + #13#10 +
      'Créela manualmente en una consola elevada:' + #13#10 +
      'netsh advfirewall firewall add rule name="' + FirewallRuleLegacy +
      '" dir=in action=allow protocol=TCP localport=5000,5001 remoteip=localsubnet profile=any',
      mbError, MB_OK);
end;

// Registra o actualiza el servicio PosBackendService sin fallar en reinstalaciones.
// 8.29-A1: AppEnvironmentExtra conserva SOLO variables NO sensibles (negocio y usuario
// semilla). Los secretos (cadena de conexión, contraseña semilla, clave JWT y
// contraseña del certificado) se escriben únicamente en secrets.json protegido.
procedure RegisterOrUpdateService(UseNssm: Boolean);
var
  AppExe, AppDir, SeedUserEnv, SeedNameEnv, BusinessEnv, ConnString: String;
  Code: Integer;
begin
  AppExe := ExpandConstant('{app}\BackendAPI\Backend.API.exe');
  AppDir := ExpandConstant('{app}\BackendAPI');
  ConnString := 'Host=' + DbPage.Values[0] + ';Port=' + DbPage.Values[1] + ';Database=' +
    DbPage.Values[2] + ';Username=' + DbPage.Values[3] + ';Password=' + DbPage.Values[4];
  SeedUserEnv := 'SystemSettings__AdminSeedUsername=' + Trim(AdminPage.Values[0]);
  SeedNameEnv := 'SystemSettings__AdminSeedName=' + Trim(AdminPage.Values[1]);
  BusinessEnv := 'SystemSettings__BusinessName=' + Trim(AdminPage.Values[4]);

  // 8.29-A1: los secretos se escriben en secrets.json protegido (el backend los carga con
  // máxima precedencia); AppEnvironmentExtra conserva solo lo no sensible (negocio/usuario).
  WriteProtectedSecretsFile(AppDir, ConnString, GetOrGenerateJwtKey, GetOrGenerateCertPass, AdminPage.Values[2]);

  if UseNssm then
  begin
    if ServiceExists then
    begin
      // Actualización: actualiza binPath, AppDirectory y reaplica env vars, luego reinicia.
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Application "' + AppExe + '"');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppDirectory "' + AppDir + '"');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Start SERVICE_AUTO_START');
      RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppEnvironmentExtra "' + SeedUserEnv + '" "' + SeedNameEnv + '" "' + BusinessEnv + '"');
      Code := RunCmd(AppDir + '\nssm.exe', 'restart ' + ServiceName);
    end
    else
    begin
      Code := RunCmd(AppDir + '\nssm.exe', 'install ' + ServiceName + ' "' + AppExe + '"');
      if Code = 0 then
      begin
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppDirectory "' + AppDir + '"');
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' Start SERVICE_AUTO_START');
        RunCmd(AppDir + '\nssm.exe', 'set ' + ServiceName + ' AppEnvironmentExtra "' + SeedUserEnv + '" "' + SeedNameEnv + '" "' + BusinessEnv + '"');
        Code := RunCmd(AppDir + '\nssm.exe', 'start ' + ServiceName);
      end;
    end;
  end
  else
  begin
    // Fallback sin NSSM: sc.exe no puede fijar variables de entorno por servicio, pero
    // los secretos ya residen en secrets.json (escrito antes), así que no se requiere
    // appsettings.Production.json con credenciales.
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

// 8.29-A1 (ex 8U-M2/M07): escribe los secretos en secrets.json con ACL restrictiva
// (SYSTEM/Administradores + NT SERVICE\<servicio> con lectura, para que el backend
// pueda leerlos bajo la Virtual Account 8U-B1). SaveStringToFile + icacls: los
// secretos nunca viajan por línea de comandos del servicio ni por argv del setup.
procedure WriteProtectedSecretsFile(AppDir: String; ConnString, JwtKey, CertPass, SeedPass: String);
var
  SecretsPath, JsonLines: String;
  Code: Integer;
begin
  SecretsPath := AppDir + '\secrets.json';
  // Formato ANIDADO compatible con el sistema de configuración .NET:
  // GetConnectionString("DefaultConnection"), config["SystemSettings:AdminSeedPassword"],
  // config["JwtSettings:Key"] y config["Kestrel:Certificates:Default:Password"].
  // Las claves planas con "__" solo funcionan en variables de entorno, no en JSON.
  JsonLines := '{' +
    '"ConnectionStrings": { "DefaultConnection": "' + JsonEsc(ConnString) + '" },' +
    '"SystemSettings": { "AdminSeedPassword": "' + JsonEsc(SeedPass) + '" },' +
    '"JwtSettings": { "Key": "' + JsonEsc(JwtKey) + '" },' +
    '"Kestrel": { "Certificates": { "Default": { "Password": "' + JsonEsc(CertPass) + '" } } }' +
    '}';

  if SaveStringToFile(SecretsPath, JsonLines, False) then
  begin
    // ACL inicial: bloqueo de herencia; SYSTEM y Administradores. La cuenta del servicio
    // (NT SERVICE\PosBackendService) se agrega DESPUÉS en Configure-PosService.ps1, cuando
    // la Virtual Account ya existe; aquí aún no está registrada (código 1132 si se intenta).
    Code := RunCmd('icacls.exe', '"' + SecretsPath + '" /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)"');
    if Code <> 0 then
      MsgBox('Aviso: no se pudieron restringir los permisos de ' + SecretsPath +
        ' (código ' + IntToStr(Code) + ').', mbInformation, MB_OK);
  end
  else
    MsgBox('Aviso: no se pudo crear el archivo de secretos protegido ' + SecretsPath + '.',
      mbInformation, MB_OK);
end;

procedure ConfigureServiceWithPowerShell;
var
  PsScript, PsParams, ConnString: String;
  Code: Integer;
begin
  PsScript := ExpandConstant('{app}\tools\Configure-PosService.ps1');
  if not FileExists(PsScript) then
  begin
    // Fallback a lógica interna si no existe el script
    ConfigureFirewall;
    RegisterOrUpdateService(HasNssm);
    Exit;
  end;

  ConnString := 'Host=' + DbPage.Values[0] + ';Port=' + DbPage.Values[1] + ';Database=' +
    DbPage.Values[2] + ';Username=' + DbPage.Values[3] + ';Password=' + DbPage.Values[4];

  // 8.29-A1: los secretos se escriben en secrets.json protegido ANTES de invocar el
  // script; el argv de PowerShell queda limpio de credenciales (sin -ConnectionString,
  // -AdminSeedPassword ni -HttpsCertPassword).
  WriteProtectedSecretsFile(ExpandConstant('{app}\BackendAPI'), ConnString,
    GetOrGenerateJwtKey, GetOrGenerateCertPass, AdminPage.Values[2]);

  PsParams := '-NoProfile -ExecutionPolicy Bypass -File "' + PsScript + '"' +
    ' -InstallDir "' + ExpandConstant('{app}') + '"' +
    ' -AdminSeedUsername "' + Trim(AdminPage.Values[0]) + '"' +
    ' -AdminSeedName "' + Trim(AdminPage.Values[1]) + '"' +
    ' -BusinessName "' + Trim(AdminPage.Values[4]) + '"';

  Code := RunCmd('powershell.exe', PsParams);
  if Code <> 0 then
  begin
    MsgBox('Aviso: La configuración del servicio reportó código ' + IntToStr(Code) + '.' + #13#10 +
      'Revise el registro detallado en:' + #13#10 +
      ExpandConstant('{app}\BackendAPI\logs\installer.log'), mbInformation, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // 1) Config de producción inicial (respaldo estático, SIN secretos; 8.29-A1)
    WriteProductionConfig;
    // 2) Configuración idempotente del servicio, firewall, secrets.json y backup
    ConfigureServiceWithPowerShell;
  end;
end;

