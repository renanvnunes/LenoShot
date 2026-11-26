; Instalador do LenoShot para Windows (Inno Setup 6).
; Normalmente chamado pelo build-windows.ps1 -Installer, que passa a versão e as pastas:
;   ISCC /DAppVersion=0.4.3 /DSourceDir=..\dist\LenoShot_0.4.3_win-x64 /DOutputDir=..\dist LenoShot.iss
;
; Instala só para o usuário atual (em %LOCALAPPDATA%\Programs, sem pedir administrador); o
; assistente oferece instalar para todos os usuários, se preferir.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\LenoShot_" + AppVersion + "_win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "LenoShot"
#define AppExe "LenoShot.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; O AppId identifica o app entre versões (atualizar em vez de instalar duplicado): nunca mude
AppId={{A061352D-A21E-48BC-9055-4A82C38A0442}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Renan Nunes
AppCopyright=© 2026 Renan Nunes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador do {#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Windows 10 1809 ou mais novo (o app usa sockets Unix para a instância única)
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=LenoShot_{#AppVersion}_win-x64-setup
SetupIconFile=LenoShot.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; O app é fechado pelo comando "quit" (ver [Run]
; Atualização silenciosa (feita pelo próprio app): abre o LenoShot de novo na bandeja
Filename: "{app}\{#AppExe}"; Parameters: "daemon"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]); o Restart Manager não precisa fazer nada
CloseApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "autostart"; Description: "Iniciar o LenoShot com o Windows (fica na bandeja, pronto para o atalho Print)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Pelo menu Iniciar o app abre nas configurações (e continua na bandeja depois)
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "settings"; Comment: "Capture, anote e compartilhe a tela"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "settings"; Tasks: desktopicon

[Registry]
; Mesmo valor que a opção "Iniciar com o sistema" das configurações do app grava
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "settings"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// Pede ao LenoShot em execução que feche (o executável não pode ser trocado com o app aberto)
procedure QuitRunningApp(const Exe: String);
var
  ResultCode: Integer;
begin
  if FileExists(Exe) then
  begin
    Exec(Exe, 'quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  QuitRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Remove o início automático mesmo se ele foi ligado pelas configurações do app.
  // As preferências em %APPDATA%\LenoShot e as capturas salvas ficam (são do usuário).
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#AppName}');
end;
