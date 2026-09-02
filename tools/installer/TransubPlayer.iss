; Transub Player — Inno Setup 6 script
; Invoked by tools\pack-release.ps1 with:
;   /DMyAppVersion=1.5.2
;   /DMyStageDir=...\artifacts\pack\_stage
;   /DMyOutDir=...\artifacts\pack
;   /DMySetupBaseName=TransubPlayer-1.5.2-win-x64-setup
;   /DMyAppIcon=...\src\TransubPlayer\Assets\app.ico
;
; Do NOT ship portable.txt in the install tree (data lives under LocalAppData).

#ifndef MyAppVersion
  #define MyAppVersion "1.5.2"
#endif
#ifndef MyStageDir
  #define MyStageDir "..\..\artifacts\pack\_stage"
#endif
#ifndef MyOutDir
  #define MyOutDir "..\..\artifacts\pack"
#endif
#ifndef MySetupBaseName
  #define MySetupBaseName "TransubPlayer-" + MyAppVersion + "-win-x64-setup"
#endif
#ifndef MyAppIcon
  #define MyAppIcon "..\..\src\TransubPlayer\Assets\app.ico"
#endif

#define MyAppName "Transub Player"
#define MyAppPublisher "Transub"
#define MyAppURL "https://www.transub.cc"
#define MyAppRepoURL "https://github.com/dlsandy/Transub-Player"
#define MyAppExeName "TransubPlayer.exe"

[Setup]
AppId={{A7C3E9F1-2B4D-4E6A-9C8F-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppRepoURL}/issues
AppUpdatesURL={#MyAppRepoURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
InfoBeforeFile=
OutputDir={#MyOutDir}
OutputBaseFilename={#MySetupBaseName}
SetupIconFile={#MyAppIcon}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Official Inno package has no ChineseSimplified.isl — vendor community translation.
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Shared stage from pack-release (no portable.txt).
Source: "{#MyStageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Transub Player\data');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除用户数据（设置、模型缓存等）？' + #13#10 + DataDir,
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
