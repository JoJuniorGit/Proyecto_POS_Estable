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
Source: "..\publish\BackendAPI\*"; DestDir: "{app}\BackendAPI"; Flags: ignoreversion recursesubdirs createallsubdirs
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

; Archivos protegidos de configuración de usuario (NUNCA sobreescribir en actualizaciones)
#if FileExists("..\publish\BackendAPI\appsettings.Production.json")
Source: "..\publish\BackendAPI\appsettings.Production.json"; DestDir: "{app}\BackendAPI"; Flags: onlyifdoesntexist uninsneveruninstall
#endif

[Dirs]
Name: "{userdocs}\Registro de cierres"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\DesktopClient\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Opción A: Registro silencioso con NSSM si nssm.exe está instalado
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "install PosBackendService ""{app}\BackendAPI\Backend.API.exe"""; Flags: runhidden; Check: HasNssm
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "set PosBackendService AppDirectory ""{app}\BackendAPI"""; Flags: runhidden; Check: HasNssm
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "set PosBackendService Start SERVICE_AUTO_START"; Flags: runhidden; Check: HasNssm
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "start PosBackendService"; Flags: runhidden; Check: HasNssm

; Opción B: Fallback silencioso usando sc.exe nativo de Windows (sin requerir nssm.exe)
Filename: "sc.exe"; Parameters: "create PosBackendService binPath= ""{app}\BackendAPI\Backend.API.exe"" start= auto"; Flags: runhidden; Check: NotHasNssm
Filename: "sc.exe"; Parameters: "start PosBackendService"; Flags: runhidden; Check: NotHasNssm

; Iniciar Cliente Desktop al finalizar el Setup
Filename: "{app}\DesktopClient\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Detención y eliminación silenciosa del servicio de Windows al desinstalar (NSSM)
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "stop PosBackendService"; Flags: runhidden; Check: HasNssm
Filename: "{app}\BackendAPI\nssm.exe"; Parameters: "remove PosBackendService confirm"; Flags: runhidden; Check: HasNssm

; Detención y eliminación silenciosa del servicio de Windows al desinstalar (sc.exe)
Filename: "sc.exe"; Parameters: "stop PosBackendService"; Flags: runhidden; Check: NotHasNssm
Filename: "sc.exe"; Parameters: "delete PosBackendService"; Flags: runhidden; Check: NotHasNssm

[Code]
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

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigFile: String;
  JsonContent: String;
  ConnString: String;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigFile := ExpandConstant('{app}\BackendAPI\appsettings.Production.json');
    if not FileExists(ConfigFile) then
    begin
      ConnString := 'Host=' + DbPage.Values[0] + ';Port=' + DbPage.Values[1] + ';Database=' + DbPage.Values[2] + ';Username=' + DbPage.Values[3] + ';Password=' + DbPage.Values[4];
      
      JsonContent := '{' + #13#10 +
        '  "ConnectionStrings": {' + #13#10 +
        '    "DefaultConnection": "' + ConnString + '"' + #13#10 +
        '  },' + #13#10 +
        '  "SystemSettings": {' + #13#10 +
        '    "MinimumClientVersion": "1.0.0",' + #13#10 +
        '    "ServerVersion": "1.0.0",' + #13#10 +
        '    "UpdateServerUrl": "' + UpdatePage.Values[0] + '",' + #13#10 +
        '    "AdminSeedUsername": "' + AdminPage.Values[0] + '",' + #13#10 +
        '    "AdminSeedPassword": "' + AdminPage.Values[1] + '",' + #13#10 +
        '    "BusinessName": "' + AdminPage.Values[2] + '"' + #13#10 +
        '  }' + #13#10 +
        '}';
        
      SaveStringToFile(ConfigFile, JsonContent, False);
    end;
  end;
end;
