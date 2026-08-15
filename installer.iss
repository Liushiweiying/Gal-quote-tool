; Gal Quote Collector — Inno Setup Script
#define MyAppName "Gal Quote Collector"
#define MyAppVersion "1.2.1"
#define MyAppPublisher "Liushiweiying"
#define MyAppURL "https://github.com/Liushiweiying/Gal-quote-tool"
#define MyAppExeName "Gal-quote-tool.exe"

[Setup]
AppId={{B4F8C1A2-3D5E-4F67-9A0B-1C2D3E4F5G6H}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir=publish-v121
OutputBaseFilename=Gal-quote-tool_Setup
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

function HexVal(C: Char): Integer;
begin
  case C of
    '0'..'9': Result := Ord(C) - Ord('0');
    'a'..'f': Result := Ord(C) - Ord('a') + 10;
    'A'..'F': Result := Ord(C) - Ord('A') + 10;
  else
    Result := 0;
  end;
end;

{ Decode JSON string escapes so CJK/Japanese directory names work. The app writes
  settings.json with the .NET default encoder, which escapes non-ASCII as \uXXXX and
  backslashes as \\ — without decoding, the uninstaller could not find (and thus not
  delete) a custom screenshot directory containing Chinese/Japanese characters. }
function JsonUnescape(const S: string): string;
var
  I, J, Code, HexLen: Integer;
begin
  Result := '';
  I := 1;
  while I <= Length(S) do
  begin
    if (S[I] = '\') and (I < Length(S)) then
    begin
      Inc(I);
      case S[I] of
        '\': Result := Result + '\';
        '"': Result := Result + '"';
        '/': Result := Result + '/';
        'b': Result := Result + #8;
        'f': Result := Result + #12;
        'n': Result := Result + #10;
        'r': Result := Result + #13;
        't': Result := Result + #9;
        'u':
        begin
          Code := 0;
          HexLen := 0;
          J := I + 1;
          while (HexLen < 4) and (J <= Length(S)) do
          begin
            case S[J] of
              '0'..'9': Code := Code * 16 + (Ord(S[J]) - Ord('0'));
              'a'..'f': Code := Code * 16 + (Ord(S[J]) - Ord('a') + 10);
              'A'..'F': Code := Code * 16 + (Ord(S[J]) - Ord('A') + 10);
            else
              Break;
            end;
            Inc(J);
            Inc(HexLen);
          end;
          if HexLen = 4 then
            Result := Result + Chr(Code)
          else
            Result := Result + '\u' + Copy(S, I + 1, HexLen);
          I := J - 1;
        end;
      else
        Result := Result + S[I];
      end;
    end
    else
      Result := Result + S[I];
    Inc(I);
  end;
end;

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
      Result := JsonUnescape(Result);
      Exit;
    end;
  end;
end;

{ ── Recycle-bin deletion ──────────────────────────────────────────
  DelTree permanently deletes; deleted data should go to the recycle bin so the
  user can change their mind. SHFileOperationW with FOF_ALLOWUNDO moves the path
  to the recycle bin; if that fails we fall back to DelTree so the uninstaller
  still removes the data. }
const
  FO_DELETE = 3;
  FOF_ALLOWUNDO = $40;
  FOF_NOCONFIRMATION = $10;
  FOF_SILENT = $4;
  FOF_NOERRORUI = $400;

type
  TSHFileOpStructW = record
    Wnd: HWND;
    wFunc: UINT;
    pFrom: string;
    pTo: string;
    fFlags: Word;
    fAnyOperationsAborted: Integer;   // BOOL in the real struct; same 4-byte layout
    hNameMappings: Integer;
    lpszProgressTitle: string;
  end;

function SHFileOperationW(var FileOp: TSHFileOpStructW): Integer;
  external 'SHFileOperationW@shell32.dll stdcall';

procedure DeleteWithRecycle(const PathName: string);
var
  FileOp: TSHFileOpStructW;
  Res: Integer;
  Aborted: Integer;
begin
  if PathName = '' then Exit;
  FileOp.Wnd := 0;
  FileOp.wFunc := FO_DELETE;
  FileOp.pFrom := PathName + #0;   // double-null-terminated (implicit + explicit)
  FileOp.pTo := '';
  FileOp.fFlags := FOF_ALLOWUNDO or FOF_NOCONFIRMATION or FOF_SILENT or FOF_NOERRORUI;
  FileOp.fAnyOperationsAborted := 0;
  FileOp.hNameMappings := 0;
  FileOp.lpszProgressTitle := '';
  Res := SHFileOperationW(FileOp);
  Aborted := FileOp.fAnyOperationsAborted;
  // Recycle failed/aborted → permanent delete as a fallback
  if (Res <> 0) or (Aborted <> 0) then
    DelTree(PathName, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  SettingsPath: string;
  ScreenDir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    DeleteData := MsgBox('是否保留语录和截图？' + #13#10 +
      '' + #13#10 +
      '「是」= 保留语录、截图和设置' + #13#10 +
      '「否」= 删除所有数据（语录、截图、设置）',
      mbConfirmation, MB_YESNO) = IDNO;

    if DeleteData then
      DeleteData := MsgBox('确定要删除所有数据？' + #13#10 +
        '' + #13#10 +
        '所有语录、截图、设置将移入回收站，可在回收站中恢复。',
        mbConfirmation, MB_YESNO) = IDYES;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    // Check both old and new data paths
    DataDir := ExpandConstant('{localappdata}') + '\GalQuoteCollector';
    if not DirExists(DataDir) then
      DataDir := ExpandConstant('{localappdata}') + '\GalgameQuoteCollector';

    SettingsPath := DataDir + '\settings.json';
    ScreenDir := ExtractScreenshotDir(SettingsPath);
    if (ScreenDir <> '') and (DirExists(ScreenDir)) then
      DeleteWithRecycle(ScreenDir);

    ScreenDir := ExpandConstant('{userpictures}') + '\GalQuoteCollector';
    if not DirExists(ScreenDir) then
      ScreenDir := ExpandConstant('{userpictures}') + '\GalgameQuoteCollector';
    if DirExists(ScreenDir) then
      DeleteWithRecycle(ScreenDir);

    if DirExists(DataDir) then
      DeleteWithRecycle(DataDir);
  end;
end;
