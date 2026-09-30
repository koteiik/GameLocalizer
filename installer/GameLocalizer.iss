#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef AppIdentity
  #error AppIdentity is required
#endif
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppIdentity + "}_is1"

[Setup]
AppId={{{#AppIdentity}}
AppName={#DisplayName}
AppVersion={#AppVersion}
AppVerName={#DisplayName} {#AppVersion}
VersionInfoVersion={#AppVersion}.0
AppPublisher=GameLocalizer contributors
AppPublisherURL=https://github.com/koteiik/GameLocalizer
AppUpdatesURL=https://github.com/koteiik/GameLocalizer/releases
DefaultDirName={localappdata}\Programs\{#DisplayName}
DefaultGroupName={#DisplayName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
UninstallDisplayName={#DisplayName}
UninstallDisplayIcon={app}\GameLocalizer.exe
Uninstallable=yes
CloseApplications=no
RestartApplications=no
SetupMutex=Local\GameLocalizer-Setup-{#AppIdentity}
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=GameLocalizer-Setup
Compression=lzma2/fast
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "update-manifest.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#InputDir}\install-files.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#InputDir}\installation.ini"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#InputDir}\installer-payload.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#DisplayName}\GameLocalizer"; Filename: "{app}\GameLocalizer.exe"; WorkingDir: "{app}"
Name: "{userprograms}\{#DisplayName}\Uninstall GameLocalizer"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#DisplayName}"; Filename: "{app}\GameLocalizer.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GameLocalizer.exe"; Description: "{cm:LaunchProgram,GameLocalizer}"; Flags: nowait postinstall skipifsilent; Check: not IsUpdate
Filename: "{app}\GameLocalizer.exe"; Parameters: "--installer-updated"; Flags: nowait; Check: LaunchAfterUpdate

[Code]
var
  OldFiles: TArrayOfString;
  DeleteUserData: Boolean;

function Attributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsUpdate: Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do if CompareText(ParamStr(I), '/UPDATE') = 0 then Result := True;
end;

function LaunchAfterUpdate: Boolean;
var I: Integer;
begin
  Result := IsUpdate;
  for I := 1 to ParamCount do if CompareText(ParamStr(I), '/NOLAUNCH') = 0 then Result := False;
end;

function NormalPath(Value: String): String;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(ExpandFileName(Value)));
end;

function InsideOrSame(Path, Root: String): Boolean;
begin
  Path := NormalPath(Path); Root := NormalPath(Root);
  Result := (Path = Root) or (Pos(AddBackslash(Root), Path) = 1);
end;

function UserDataPath: String;
begin
  Result := ExpandConstant('{localappdata}\{#DataDirectoryName}');
end;

function NoLinks(Path: String): Boolean;
var Parent: String; Attr: LongWord;
begin
  Result := True;
  repeat
    Attr := Attributes(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin Result := False; Exit; end;
    Parent := ExtractFileDir(Path);
    if Parent = Path then Exit;
    Path := Parent;
  until Path = '';
end;

function RegisteredPath: String;
begin
  if not RegQueryStringValue(HKCU64, '{#UninstallKey}', 'InstallLocation', Result) then Result := '';
end;

function VersionAllowed: Boolean;
var OldVersion: String; PackedOld, PackedNew: Int64;
begin
  Result := True;
  if RegQueryStringValue(HKCU64, '{#UninstallKey}', 'DisplayVersion', OldVersion) then begin
    if not StrToVersion(OldVersion, PackedOld) then begin Result := False; Exit; end;
    StrToVersion('{#AppVersion}', PackedNew);
    if ComparePackedVersion(PackedOld, PackedNew) > 0 then begin
      Result := False;
      if not WizardSilent then Result := MsgBox('Установлена более новая версия ' + OldVersion + '. Установить более старую {#AppVersion}?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := VersionAllowed;
  if not Result then Log('Downgrade or invalid installed version rejected.');
  if IsUpdate and (RegisteredPath = '') then begin Log('Update requires a registered installation.'); Result := False; end;
end;

function RelativeOwnedFile(Name: String): Boolean;
var Part: String; Sep: Integer;
begin
  Result := False;
  if (Name = '') or (Pos('/', Name) > 0) or (Pos(':', Name) > 0) or (Name[1] = '\') then Exit;
  while Name <> '' do begin
    Sep := Pos('\', Name); if Sep = 0 then Sep := Length(Name) + 1;
    Part := Copy(Name, 1, Sep - 1); Delete(Name, 1, Sep);
    if (Part = '') or (Part = '.') or (Part = '..') or (Pos('*', Part) > 0) or (Pos('?', Part) > 0) then Exit;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var App, Registered: String; I, Attempts: Integer; Find: TFindRec;
begin
  Result := ''; App := ExpandConstant('{app}'); Registered := RegisteredPath;
  if not NoLinks(App) or InsideOrSame(App, UserDataPath) or InsideOrSame(UserDataPath, App) or
      (NormalPath(App) = NormalPath(ExpandConstant('{src}'))) or (Length(App) < 5) then begin
    Result := 'Выберите отдельную папку программы вне пользовательских данных и папки установщика.'; Exit;
  end;
  if (Registered <> '') and (NormalPath(Registered) <> NormalPath(App)) then begin
    Result := 'Обновление должно использовать существующую папку установки. Для переноса сначала удалите программу, сохранив данные.'; Exit;
  end;
  if Registered = '' then begin
    if FindFirst(AddBackslash(App) + '*', Find) then begin
      try
        repeat
          if (Find.Name <> '.') and (Find.Name <> '..') then begin Result := 'Для новой установки выберите пустую папку.'; Break; end;
        until not FindNext(Find);
      finally FindClose(Find); end;
    end;
    if Result <> '' then Exit;
  end;
  Attempts := 0;
  while CheckForMutexes('Local\GameLocalizer-{#AppIdentity}') do begin
    if (not IsUpdate) or (Attempts >= 600) then begin Result := 'Закройте GameLocalizer и повторите установку.'; Exit; end;
    Sleep(100); Attempts := Attempts + 1;
  end;
  SetArrayLength(OldFiles, 0);
  if Registered <> '' then begin
    if not NoLinks(App + '\install-files.txt') or not LoadStringsFromFile(App + '\install-files.txt', OldFiles) then begin
      Result := 'Список файлов предыдущей установки отсутствует или небезопасен. Выполните восстановительную установку или удалите программу с сохранением данных.'; Exit;
    end;
    for I := 0 to GetArrayLength(OldFiles) - 1 do
      if not RelativeOwnedFile(OldFiles[I]) or not NoLinks(AddBackslash(App) + OldFiles[I]) then begin Result := 'Недопустимый путь в списке файлов установки.'; Exit; end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var NewFiles: TArrayOfString; I, J: Integer; Found: Boolean; FileName: String;
begin
  if CurStep = ssPostInstall then begin
    if not LoadStringsFromFile(ExpandConstant('{app}\install-files.txt'), NewFiles) then RaiseException('Не найден новый список файлов.');
    for I := 0 to GetArrayLength(OldFiles) - 1 do begin
      Found := False;
      for J := 0 to GetArrayLength(NewFiles) - 1 do if CompareText(OldFiles[I], NewFiles[J]) = 0 then begin Found := True; Break; end;
      if not Found then begin
        FileName := AddBackslash(ExpandConstant('{app}')) + OldFiles[I];
        if not NoLinks(FileName) then RaiseException('Небезопасный путь устаревшего файла.');
        if FileExists(FileName) and not DeleteFile(FileName) then RaiseException('Не удалось удалить устаревший файл: ' + OldFiles[I]);
        Log('Removed obsolete owned file: ' + OldFiles[I]);
      end;
    end;
  end;
end;

function TreeHasLinks(Path: String): Boolean;
var Find: TFindRec;
begin
  Result := not NoLinks(Path);
  if Result then Exit;
  if FindFirst(AddBackslash(Path) + '*', Find) then begin
    try
      repeat
        if (Find.Name <> '.') and (Find.Name <> '..') then begin
          if (Find.Attributes and $400) <> 0 then Result := True
          else if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := TreeHasLinks(AddBackslash(Path) + Find.Name);
          if Result then Break;
        end;
      until not FindNext(Find);
    finally FindClose(Find); end;
  end;
end;

function InitializeUninstall: Boolean;
var I: Integer;
begin
  Result := not CheckForMutexes('Local\GameLocalizer-{#AppIdentity}');
  if not Result then begin SuppressibleMsgBox('Закройте GameLocalizer перед удалением.', mbError, MB_OK, IDOK); Exit; end;
  DeleteUserData := False;
  { An explicit command-line consent is required for unattended data removal. }
  for I := 1 to ParamCount do if CompareText(ParamStr(I), '/DELETEUSERDATA') = 0 then DeleteUserData := True;
  if not UninstallSilent then DeleteUserData := MsgBox('Удалить пользовательские данные и модели?' + #13#10 + UserDataPath + #13#10 + 'Память переводов, настройки и модели будут удалены. По умолчанию данные сохраняются.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then begin
    if TreeHasLinks(UserDataPath) then begin
      SuppressibleMsgBox('Данные сохранены: обнаружена ссылка или junction. Удалите их вручную.', mbError, MB_OK, IDOK); Exit;
    end;
    if not DelTree(UserDataPath, True, True, True) then SuppressibleMsgBox('Не все пользовательские данные удалось удалить. Закройте использующие их программы.', mbError, MB_OK, IDOK);
  end;
end;
