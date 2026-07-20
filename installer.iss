; Gal Quote Collector — Inno Setup Script
#define MyAppName "Gal Quote Collector"
#define MyAppVersion "1.1.6"
#define MyAppPublisher "Liushiweiying"
#define MyAppURL "https://github.com/Liushiweiying/Gal-quote-tool"
#define MyAppExeName "GalQuoteCollector.exe"

[Setup]
AppId={{B4F8C1A2-3D5E-4F67-9A0B-1C2D3E4F5G6H}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir=publish-v140
OutputBaseFilename=GalQuoteCollector_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Dirs]
Name: "{app}"; Flags: uninsalwaysuninstall
Name: "{app}\runtimes"; Flags: uninsalwaysuninstall

[Files]
Source: "publish-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall nowait skipifsilent

[Code]
var
  DeleteData: Boolean;

function ExtractScreenshotDir(const JsonPath: string): string;
var
  Lines: TArrayOfString;
  I: Integer;
  P: Integer;
begin
  Result := '';
  if not FileExists(JsonPath) then Exit;
  if not LoadStringsFromFile(JsonPath, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    P := Pos('"ScreenshotDirectory"', Lines[I]);
    if P > 0 then
    begin
      Result := Copy(Lines[I], P + 22, Length(Lines[I]));
      Result := Trim(Result);
      if Copy(Result, 1, 1) = '"' then Result := Copy(Result, 2, Length(Result));
      P := Pos('"', Result);
      if P > 0 then Result := Copy(Result, 1, P - 1);
      Exit;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  SettingsPath: string;
  ScreenDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteData := MsgBox('是否保留数据？' + #13#10 +
      '' + #13#10 +
      '「是」= 保留数据库、截图、设置' + #13#10 +
      '「否」= 删除所有数据',
      mbConfirmation, MB_YESNO) = IDNO;

    if DeleteData then
      DeleteData := MsgBox('确定要删除所有数据？' + #13#10 +
        '' + #13#10 +
        '此操作不可撤销！所有语录、截图、设置将永久删除。',
        mbConfirmation, MB_YESNO) = IDYES;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    DataDir := ExpandConstant('{localappdata}') + '\GalQuoteCollector';

    SettingsPath := DataDir + '\settings.json';
    ScreenDir := ExtractScreenshotDir(SettingsPath);
    if (ScreenDir <> '') and (DirExists(ScreenDir)) then
      DelTree(ScreenDir, True, True, True);

    ScreenDir := ExpandConstant('{userpictures}') + '\GalQuoteCollector';
    if DirExists(ScreenDir) then
      DelTree(ScreenDir, True, True, True);

    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
