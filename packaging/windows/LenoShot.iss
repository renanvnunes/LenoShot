; Instalador do LenoShot (Inno Setup).
; Uso: iscc /DAppVersion=0.6.0 /DSourceDir=<pasta do publish> packaging\windows\LenoShot.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\publish"
#endif

[Setup]
AppId={{6C1E5B0A-3B7E-4F55-9F0B-5A1D2E7C9A10}
AppName=LenoShot
AppVersion={#AppVersion}
AppPublisher=Renan Nunes
DefaultDirName={autopf}\LenoShot
DefaultGroupName=LenoShot
UninstallDisplayIcon={app}\LenoShot.exe
OutputDir=..\..\dist
OutputBaseFilename=LenoShot_{#AppVersion}_win-x64-setup
LicenseFile=..\..\LICENSE
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
ChangesEnvironment=yes
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "*.pdb"

[Icons]
Name: "{group}\LenoShot"; Filename: "{app}\LenoShot.exe"; Parameters: "settings"
Name: "{autodesktop}\LenoShot"; Filename: "{app}\LenoShot.exe"; Parameters: "settings"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked
Name: "addtopath"; Description: "Adicionar ao PATH (usar o comando ""LenoShot"" no terminal)"

[Registry]
; Alias de linha de comando: LenoShot capture, LenoShot settings, ...
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; \
    ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
function NeedsAddPath(Param: string): Boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then
  begin
    Result := True;
    Exit;
  end;
  Result := Pos(';' + Lowercase(Param) + ';', ';' + Lowercase(OrigPath) + ';') = 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath, AppDir: string;
  P: Integer;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath) then Exit;
  AppDir := ExpandConstant('{app}');
  P := Pos(';' + Lowercase(AppDir), ';' + Lowercase(OrigPath));
  if P > 0 then
  begin
    Delete(OrigPath, P, Length(AppDir) + 1);
    RegWriteExpandStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', OrigPath);
  end;
end;

[Run]
Filename: "{app}\LenoShot.exe"; Parameters: "settings"; Description: "Abrir o LenoShot"; Flags: nowait postinstall skipifsilent
