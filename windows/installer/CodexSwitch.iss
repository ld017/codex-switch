#define AppName "Codex Switch"
#define AppVersion "1.0.0"
#define AppExeName "CodexSwitch.exe"

[Setup]
AppId={{A539EF52-C987-430E-8996-CC1A9D2B9D37}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ld017
DefaultDirName={localappdata}\Programs\Codex Switch
DefaultGroupName=Codex Switch
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=CodexSwitch-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\CodexSwitch.App\Resources\CodexSwitch.ico
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "登录 Windows 时自动启动 Codex Switch"; GroupDescription: "其他选项："; Flags: unchecked

[Files]
Source: "..\..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Codex Switch"; Filename: "{app}\{#AppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CodexSwitch"; ValueData: """{app}\{#AppExeName}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 Codex Switch"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v CodexSwitch /f"; Flags: runhidden; RunOnceId: "RemoveStartupEntry"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and (not UninstallSilent) then
    if MsgBox('是否同时删除当前用户的 Codex Switch 加密账号、缓存和日志？选择“否”可在以后重新安装时继续使用。', mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{localappdata}\CodexSwitch'), True, True, True);
end;
