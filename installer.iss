; Galgame Quote Collector — Inno Setup Script
#define MyAppName "Galgame Quote Collector"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Liushiweiying"
#define MyAppURL "https://github.com/Liushiweiying/Galgame-quote-tool"
#define MyAppExeName "GalgameQuoteCollector_selfcontained.exe"

[Setup]
AppId={{B4F8C1A2-3D5E-4F67-9A0B-1C2D3E4F5G6H}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir=publish-v105
OutputBaseFilename=GalgameQuoteCollector_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "publish-v105\GalgameQuoteCollector_selfcontained.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.zh.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.ja.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent

[Code]
var
  DeleteData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteData := MsgBox('是否保留语录数据？' + #13#10 +
      '' + #13#10 +
      '「是」= 保留数据库、截图、设置文件' + #13#10 +
      '「否」= 同时删除所有数据',
      mbConfirmation, MB_YESNO) = IDNO;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    DataDir := ExpandConstant('{localappdata}') + '\GalgameQuoteCollector';
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
